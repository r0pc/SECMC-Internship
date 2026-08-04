using System.Diagnostics;
using System.Net;
using System.Text;
using DataIntelligence.Core.Dtos;
using DataIntelligence.Core.Entities;
using DataIntelligence.Core.Enums;
using DataIntelligence.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataIntelligence.Infrastructure.Collection;

/// <summary>
/// Executes a source request with a timeout, bounded retries and exponential backoff.
/// </summary>
/// <remarks>
/// Publisher-agnostic: the adapter decides what to send, this decides how to send it and how to
/// classify what comes back. Failures are returned rather than thrown (FR-2), so the caller
/// records the run and the scheduler survives.
/// </remarks>
public sealed class HttpSourceFetcher : ISourceFetcher
{
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

    public async Task<FetchResult> FetchAsync(
        SourceRequest request, DataSource source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(source);

        if (string.IsNullOrWhiteSpace(request.Url))
        {
            return FetchResult.Failure(CollectionFailureCategory.Unknown,
                $"Source '{source.Code}' produced an empty request URL.");
        }

        // Per-source retry budget: the two publishers have different reliability profiles and
        // different quotas, so one is not made to inherit the other's policy.
        var maxAttempts = Math.Max(1, (int)source.MaxRetries) + 1;
        FetchResult? lastFailure = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await TryFetchOnceAsync(request, source, attempt, cancellationToken);
            if (result.Succeeded)
            {
                return result;
            }

            lastFailure = result;

            if (!IsTransient(result) || attempt == maxAttempts)
            {
                break;
            }

            var delay = TimeSpan.FromSeconds(_options.RetryBaseDelaySeconds * Math.Pow(2, attempt - 1));
            _logger.LogWarning(
                "{Source}: fetch attempt {Attempt}/{MaxAttempts} failed ({Category}): {Message}. Retrying in {Delay}.",
                source.Code, attempt, maxAttempts, result.FailureCategory, result.ErrorMessage, delay);

            await Task.Delay(delay, cancellationToken);
        }

        return lastFailure!;
    }

    private async Task<FetchResult> TryFetchOnceAsync(
        SourceRequest request, DataSource source, int attempt, CancellationToken cancellationToken)
    {
        var timeoutSeconds = source.RequestTimeoutSec > 0
            ? source.RequestTimeoutSec
            : _options.RequestTimeoutSeconds;

        // Separate token so a request timeout stays distinguishable from service shutdown: both
        // surface as OperationCanceledException, but only one of them is a collection failure.
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var httpRequest = new HttpRequestMessage(request.Method, request.Url);

            if (request.JsonBody is { Length: > 0 } body)
            {
                httpRequest.Content = new StringContent(body, Encoding.UTF8, "application/json");
            }

            using var response = await _httpClient.SendAsync(
                httpRequest, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token);

            var statusCode = (short)response.StatusCode;

            if (!response.IsSuccessStatusCode)
            {
                // 429 is categorised separately: the remedy is a smaller query budget or a
                // registration key, not a faster retry.
                var category = response.StatusCode == HttpStatusCode.TooManyRequests
                    ? CollectionFailureCategory.RateLimited
                    : CollectionFailureCategory.HttpError;

                return FetchResult.Failure(category,
                    $"{source.Code} returned {statusCode} {response.ReasonPhrase}.",
                    detail: $"{request.Method} {request.Url}",
                    statusCode: statusCode,
                    attempts: attempt);
            }

            var maxBytes = (long)_options.MaxPayloadMegabytes * 1024 * 1024;

            if (response.Content.Headers.ContentLength is { } declared && declared > maxBytes)
            {
                return FetchResult.Failure(CollectionFailureCategory.HttpError,
                    $"Payload declares {declared} bytes, over the {_options.MaxPayloadMegabytes} MB limit.",
                    detail: "A sudden size jump usually means an error page, not real data.",
                    statusCode: statusCode, attempts: attempt);
            }

            var (content, exceededLimit) = await ReadBoundedAsync(response, maxBytes, linkedCts.Token);

            if (exceededLimit)
            {
                return FetchResult.Failure(CollectionFailureCategory.HttpError,
                    $"Payload exceeded the {_options.MaxPayloadMegabytes} MB limit while streaming.",
                    statusCode: statusCode, attempts: attempt);
            }

            _logger.LogInformation(
                "{Source}: fetched {Bytes} bytes in {ElapsedMs} ms (attempt {Attempt}).",
                source.Code, content.Length, stopwatch.ElapsedMilliseconds, attempt);

            return FetchResult.Success(
                content, response.Content.Headers.ContentType?.ToString(), statusCode, attempt);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Service shutdown, not a source failure. Let the host stop cleanly.
            throw;
        }
        catch (OperationCanceledException)
        {
            return FetchResult.Failure(CollectionFailureCategory.Timeout,
                $"Request exceeded the {timeoutSeconds}s timeout.",
                detail: $"{request.Method} {request.Url}", attempts: attempt);
        }
        catch (HttpRequestException ex)
        {
            return FetchResult.Failure(CategoriseHttpError(ex), ex.Message,
                detail: ex.ToString(),
                statusCode: ex.StatusCode is { } code ? (short)code : null,
                attempts: attempt);
        }
        catch (Exception ex)
        {
            return FetchResult.Failure(CollectionFailureCategory.Unknown, ex.Message,
                detail: ex.ToString(), attempts: attempt);
        }
    }

    /// <summary>
    /// Streams the body, stopping as soon as the cap is passed, so an unexpectedly huge response
    /// cannot exhaust memory on the worker host.
    /// </summary>
    private static async Task<(string Content, bool ExceededLimit)> ReadBoundedAsync(
        HttpResponseMessage response, long maxBytes, CancellationToken cancellationToken)
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
    /// Retry only what a retry could plausibly fix. Connection problems and timeouts qualify;
    /// most status codes do not — a 404 fails identically three more times. 408, 429 and 5xx are
    /// the exceptions, being the server asking us to come back.
    /// </summary>
    private static bool IsTransient(FetchResult result) => result.FailureCategory switch
    {
        CollectionFailureCategory.Unreachable or CollectionFailureCategory.Timeout => true,
        CollectionFailureCategory.RateLimited => false,
        CollectionFailureCategory.HttpError => result.HttpStatusCode is null
            or (short)HttpStatusCode.RequestTimeout
            or >= 500 and < 600,
        _ => false
    };
}
