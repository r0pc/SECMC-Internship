using System.Diagnostics;
using System.Net;
using System.Text;
using DataIntelligence.Core.Dtos;
using DataIntelligence.Core.Enums;
using DataIntelligence.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataIntelligence.Infrastructure.Collection;

/// <summary>
/// Fetches the source over HTTP with a timeout, bounded retries, and exponential backoff.
/// </summary>
/// <remarks>
/// Returns failures rather than throwing them (FR-2): the caller records the run and the
/// scheduler survives. Retries are hand-rolled rather than taken from a resilience package —
/// the policy needed here is a handful of lines, and it keeps the dependency surface of a
/// scheduled service that runs unattended as small as possible.
/// </remarks>
public sealed class HttpSourceFetcher : ISourceFetcher
{
    /// <summary>Named client so its handler lifetime and headers are configured in one place.</summary>
    public const string HttpClientName = "SourceCollector";

    private readonly HttpClient _httpClient;
    private readonly CollectionOptions _options;
    private readonly ILogger<HttpSourceFetcher> _logger;

    public HttpSourceFetcher(
        HttpClient httpClient,
        IOptions<CollectionOptions> options,
        ILogger<HttpSourceFetcher> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<FetchResult> FetchAsync(string url, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return FetchResult.Failure(CollectionFailureCategory.Unknown,
                "No source URL is configured.");
        }

        var maxAttempts = _options.MaxRetries + 1;
        FetchResult? lastFailure = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await TryFetchOnceAsync(url, attempt, cancellationToken);
            if (result.Succeeded)
            {
                return result;
            }

            lastFailure = result;

            if (!IsTransient(result) || attempt == maxAttempts)
            {
                break;
            }

            // 2s, 4s, 8s. Bounded by MaxRetries, so a source that is down cannot hold the
            // cycle open indefinitely — the run is recorded as failed and the next hour retries.
            var delay = TimeSpan.FromSeconds(_options.RetryBaseDelaySeconds * Math.Pow(2, attempt - 1));
            _logger.LogWarning(
                "Fetch attempt {Attempt}/{MaxAttempts} failed ({Category}): {Message}. Retrying in {Delay}.",
                attempt, maxAttempts, result.FailureCategory, result.ErrorMessage, delay);

            await Task.Delay(delay, cancellationToken);
        }

        return lastFailure!;
    }

    private async Task<FetchResult> TryFetchOnceAsync(string url, int attempt, CancellationToken cancellationToken)
    {
        // Separate token so a request timeout is distinguishable from service shutdown: both
        // surface as TaskCanceledException, but only one of them is a collection failure.
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_options.RequestTimeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await _httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token);

            var statusCode = (short)response.StatusCode;

            if (!response.IsSuccessStatusCode)
            {
                return FetchResult.Failure(
                    CollectionFailureCategory.HttpError,
                    $"Source returned {statusCode} {response.ReasonPhrase}.",
                    detail: $"GET {url}",
                    statusCode: statusCode,
                    attempts: attempt);
            }

            var maxBytes = (long)_options.MaxPayloadMegabytes * 1024 * 1024;

            // Trust the declared length when it is present — cheaper than downloading to find out.
            if (response.Content.Headers.ContentLength is { } declared && declared > maxBytes)
            {
                return FetchResult.Failure(
                    CollectionFailureCategory.HttpError,
                    $"Payload declares {declared} bytes, over the {_options.MaxPayloadMegabytes} MB limit.",
                    detail: "A sudden size jump usually means an error page or a redirect loop, not real data.",
                    statusCode: statusCode,
                    attempts: attempt);
            }

            var (content, exceededLimit) = await ReadBoundedAsync(response, maxBytes, linkedCts.Token);

            if (exceededLimit)
            {
                return FetchResult.Failure(
                    CollectionFailureCategory.HttpError,
                    $"Payload exceeded the {_options.MaxPayloadMegabytes} MB limit while streaming.",
                    statusCode: statusCode,
                    attempts: attempt);
            }

            _logger.LogInformation(
                "Fetched {Bytes} bytes from {Url} in {ElapsedMs} ms (attempt {Attempt}).",
                content.Length, url, stopwatch.ElapsedMilliseconds, attempt);

            return FetchResult.Success(
                content,
                response.Content.Headers.ContentType?.ToString(),
                statusCode,
                attempt);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Service shutdown, not a source failure. Let the host stop cleanly.
            throw;
        }
        catch (OperationCanceledException)
        {
            return FetchResult.Failure(
                CollectionFailureCategory.Timeout,
                $"Request exceeded the {_options.RequestTimeoutSeconds}s timeout.",
                detail: $"GET {url}",
                attempts: attempt);
        }
        catch (HttpRequestException ex)
        {
            return FetchResult.Failure(
                CategoriseHttpError(ex),
                ex.Message,
                detail: ex.ToString(),
                statusCode: ex.StatusCode is { } code ? (short)code : null,
                attempts: attempt);
        }
        catch (Exception ex)
        {
            return FetchResult.Failure(
                CollectionFailureCategory.Unknown,
                ex.Message,
                detail: ex.ToString(),
                attempts: attempt);
        }
    }

    /// <summary>
    /// Streams the body, stopping as soon as the cap is passed, so an unexpectedly huge
    /// response cannot exhaust memory on the worker host.
    /// </summary>
    private static async Task<(string Content, bool ExceededLimit)> ReadBoundedAsync(
        HttpResponseMessage response,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();

        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return (string.Empty, true);
            }

            buffer.Write(chunk, 0, read);
        }

        return (ResolveEncoding(response).GetString(buffer.ToArray()), false);
    }

    /// <summary>
    /// Honours the charset the source declares. Falling back to UTF-8 blindly mangles accented
    /// text on sources that still publish ISO-8859-1.
    /// </summary>
    private static Encoding ResolveEncoding(HttpResponseMessage response)
    {
        var charset = response.Content.Headers.ContentType?.CharSet;
        if (string.IsNullOrWhiteSpace(charset))
        {
            return Encoding.UTF8;
        }

        try
        {
            return Encoding.GetEncoding(charset.Trim('"'));
        }
        catch (ArgumentException)
        {
            // Unknown or unregistered code page. UTF-8 is the safer guess than failing the run.
            return Encoding.UTF8;
        }
    }

    private static CollectionFailureCategory CategoriseHttpError(HttpRequestException ex) =>
        ex.HttpRequestError switch
        {
            HttpRequestError.NameResolutionError
                or HttpRequestError.ConnectionError
                or HttpRequestError.SecureConnectionError
                or HttpRequestError.ProxyTunnelError => CollectionFailureCategory.Unreachable,
            HttpRequestError.ResponseEnded
                or HttpRequestError.InvalidResponse
                or HttpRequestError.HttpProtocolError => CollectionFailureCategory.HttpError,
            _ => ex.StatusCode is not null
                ? CollectionFailureCategory.HttpError
                : CollectionFailureCategory.Unreachable
        };

    /// <summary>
    /// Retry only what a retry could plausibly fix.
    /// </summary>
    /// <remarks>
    /// Connection problems and timeouts are worth another attempt. HTTP status codes mostly are
    /// not: a 404 or a 403 will fail identically three more times, so retrying only delays the
    /// failure record and holds the cycle open. The exceptions are 408, 429 and 5xx, which the
    /// server itself is telling us to come back for.
    /// </remarks>
    private static bool IsTransient(FetchResult result) => result.FailureCategory switch
    {
        CollectionFailureCategory.Unreachable or CollectionFailureCategory.Timeout => true,
        CollectionFailureCategory.HttpError => result.HttpStatusCode is null
            or (short)HttpStatusCode.RequestTimeout
            or (short)HttpStatusCode.TooManyRequests
            or >= 500 and < 600,
        _ => false
    };
}
