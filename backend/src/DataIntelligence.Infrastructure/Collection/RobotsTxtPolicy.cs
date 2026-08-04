using System.Globalization;
using DataIntelligence.Core.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataIntelligence.Infrastructure.Collection;

/// <summary>
/// Evaluates the source's robots.txt before any collection request, so data collection
/// respects the site's directives (SOW 3 — Compliance).
/// </summary>
/// <remarks>
/// Implements the subset of RFC 9309 that matters for a single-URL hourly collector: user-agent
/// group selection, Allow/Disallow with longest-match-wins, and Crawl-delay. Wildcards
/// (<c>*</c>) and end-anchors (<c>$</c>) in paths are supported because they are common in
/// practice. Not implemented: Sitemap directives, which are irrelevant here.
/// <para>
/// On an unreachable robots.txt the decision is <em>disallow</em>. That is the fail-closed
/// reading of RFC 9309 §2.3.1.4 and the right default for a compliance control — a run that is
/// wrongly skipped is recoverable, a run that wrongly scrapes is not.
/// </para>
/// </remarks>
public sealed class RobotsTxtPolicy : IRobotsPolicy
{
    private readonly HttpClient _httpClient;
    private readonly IMemoryCache _cache;
    private readonly CollectionOptions _options;
    private readonly ILogger<RobotsTxtPolicy> _logger;

    public RobotsTxtPolicy(
        HttpClient httpClient,
        IMemoryCache cache,
        IOptions<CollectionOptions> options,
        ILogger<RobotsTxtPolicy> logger)
    {
        _httpClient = httpClient;
        _cache = cache;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<RobotsDecision> EvaluateAsync(string url, CancellationToken cancellationToken)
    {
        if (!_options.RespectRobotsTxt)
        {
            return RobotsDecision.Allowed("robots.txt enforcement is disabled by configuration.");
        }

        // The scheme check is not redundant. On Unix, Uri.TryCreate accepts a bare path like
        // "/data/listings" as an absolute *file* URI, so an absolute-only test passes on Windows
        // and silently lets a misconfigured URL through on the Linux hosts this deploys to.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var target)
            || (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps))
        {
            return RobotsDecision.Disallowed($"'{url}' is not an absolute http or https URL.");
        }

        var rules = await GetRulesAsync(target, cancellationToken);
        return rules.Evaluate(target.PathAndQuery, _options.UserAgent);
    }

    private async Task<RobotsRuleSet> GetRulesAsync(Uri target, CancellationToken cancellationToken)
    {
        var cacheKey = $"robots:{target.Scheme}://{target.Authority}";

        if (_cache.TryGetValue<RobotsRuleSet>(cacheKey, out var cached) && cached is not null)
        {
            return cached;
        }

        var rules = await FetchRulesAsync(target, cancellationToken);

        _cache.Set(cacheKey, rules, TimeSpan.FromMinutes(_options.RobotsCacheMinutes));
        return rules;
    }

    private async Task<RobotsRuleSet> FetchRulesAsync(Uri target, CancellationToken cancellationToken)
    {
        var robotsUrl = new Uri(target, "/robots.txt");

        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            using var response = await _httpClient.GetAsync(robotsUrl, linked.Token);

            // 4xx means "no robots.txt published", which RFC 9309 treats as full allowance.
            if ((int)response.StatusCode is >= 400 and < 500)
            {
                _logger.LogInformation(
                    "No robots.txt at {RobotsUrl} ({StatusCode}); collection is unrestricted.",
                    robotsUrl, (int)response.StatusCode);
                return RobotsRuleSet.AllowAll($"robots.txt returned {(int)response.StatusCode}.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return RobotsRuleSet.DenyAll(
                    $"robots.txt returned {(int)response.StatusCode}; treating as disallowed.");
            }

            var content = await response.Content.ReadAsStringAsync(linked.Token);
            return RobotsRuleSet.Parse(content);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not retrieve {RobotsUrl}. Collection is disallowed until it can be read.",
                robotsUrl);
            return RobotsRuleSet.DenyAll($"robots.txt could not be retrieved: {ex.Message}");
        }
    }
}

/// <summary>Parsed robots.txt directives for one host.</summary>
internal sealed class RobotsRuleSet
{
    private readonly Dictionary<string, List<RobotsRule>> _groups;
    private readonly Dictionary<string, TimeSpan> _crawlDelays;
    private readonly bool? _blanketDecision;
    private readonly string _blanketReason;

    private RobotsRuleSet(
        Dictionary<string, List<RobotsRule>> groups,
        Dictionary<string, TimeSpan> crawlDelays,
        bool? blanketDecision = null,
        string blanketReason = "")
    {
        _groups = groups;
        _crawlDelays = crawlDelays;
        _blanketDecision = blanketDecision;
        _blanketReason = blanketReason;
    }

    public static RobotsRuleSet AllowAll(string reason) => new([], [], true, reason);

    public static RobotsRuleSet DenyAll(string reason) => new([], [], false, reason);

    public static RobotsRuleSet Parse(string content)
    {
        var groups = new Dictionary<string, List<RobotsRule>>(StringComparer.OrdinalIgnoreCase);
        var crawlDelays = new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase);

        // Consecutive User-agent lines share the rules that follow them, so agents accumulate
        // until the first rule line closes the group header.
        var currentAgents = new List<string>();
        var expectingAgents = true;

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine;

            var commentAt = line.IndexOf('#');
            if (commentAt >= 0)
            {
                line = line[..commentAt];
            }

            line = line.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var separator = line.IndexOf(':');
            if (separator < 0)
            {
                continue;
            }

            var directive = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();

            if (directive.Equals("User-agent", StringComparison.OrdinalIgnoreCase))
            {
                if (!expectingAgents)
                {
                    currentAgents.Clear();
                    expectingAgents = true;
                }

                if (value.Length > 0)
                {
                    currentAgents.Add(value);
                    groups.TryAdd(value, []);
                }

                continue;
            }

            if (currentAgents.Count == 0)
            {
                // A rule before any User-agent line is malformed; ignore it.
                continue;
            }

            expectingAgents = false;

            if (directive.Equals("Disallow", StringComparison.OrdinalIgnoreCase))
            {
                // An empty Disallow means "allow everything" for this group.
                foreach (var agent in currentAgents)
                {
                    groups[agent].Add(new RobotsRule(value, IsAllow: value.Length == 0));
                }
            }
            else if (directive.Equals("Allow", StringComparison.OrdinalIgnoreCase))
            {
                if (value.Length > 0)
                {
                    foreach (var agent in currentAgents)
                    {
                        groups[agent].Add(new RobotsRule(value, IsAllow: true));
                    }
                }
            }
            else if (directive.Equals("Crawl-delay", StringComparison.OrdinalIgnoreCase)
                && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                && seconds is > 0 and < 86400)
            {
                foreach (var agent in currentAgents)
                {
                    crawlDelays[agent] = TimeSpan.FromSeconds(seconds);
                }
            }
        }

        return new RobotsRuleSet(groups, crawlDelays);
    }

    public RobotsDecision Evaluate(string path, string userAgent)
    {
        if (_blanketDecision is { } blanket)
        {
            return blanket
                ? RobotsDecision.Allowed(_blanketReason)
                : RobotsDecision.Disallowed(_blanketReason);
        }

        var group = SelectGroup(userAgent);
        if (group is null)
        {
            return RobotsDecision.Allowed("No robots.txt group matches this user agent.");
        }

        var (agentName, rules) = group.Value;
        _crawlDelays.TryGetValue(agentName, out var crawlDelay);
        var delay = crawlDelay == default ? (TimeSpan?)null : crawlDelay;

        // RFC 9309 §2.2.2: the most specific (longest) matching rule wins; Allow wins ties.
        RobotsRule? best = null;
        foreach (var rule in rules)
        {
            if (!rule.Matches(path))
            {
                continue;
            }

            if (best is null
                || rule.Path.Length > best.Path.Length
                || (rule.Path.Length == best.Path.Length && rule.IsAllow))
            {
                best = rule;
            }
        }

        if (best is null)
        {
            return RobotsDecision.Allowed(
                $"No rule in the '{agentName}' group matches {path}.", delay);
        }

        return best.IsAllow
            ? RobotsDecision.Allowed(
                $"Allowed by '{agentName}' rule '{(best.Path.Length == 0 ? "Disallow:" : "Allow: " + best.Path)}'.", delay)
            : RobotsDecision.Disallowed(
                $"Disallowed by '{agentName}' rule 'Disallow: {best.Path}'.");
    }

    /// <summary>
    /// Exact user-agent match first, then the <c>*</c> group — RFC 9309 §2.2.1. A crawler must
    /// obey its own group and ignore <c>*</c> entirely when one exists.
    /// </summary>
    private (string Agent, List<RobotsRule> Rules)? SelectGroup(string userAgent)
    {
        // The product token is the part before any '/', e.g. "DataIntelligencePlatform/1.0".
        var token = userAgent.Split('/')[0].Trim();

        foreach (var (agent, rules) in _groups)
        {
            if (agent.Equals(token, StringComparison.OrdinalIgnoreCase))
            {
                return (agent, rules);
            }
        }

        return _groups.TryGetValue("*", out var wildcard) ? ("*", wildcard) : null;
    }
}

/// <param name="Path">The rule's path pattern; may contain <c>*</c> and a trailing <c>$</c>.</param>
/// <param name="IsAllow">True for Allow, and for the empty Disallow that means "allow all".</param>
internal sealed record RobotsRule(string Path, bool IsAllow)
{
    public bool Matches(string path)
    {
        if (Path.Length == 0)
        {
            return true;
        }

        var pattern = Path;
        var anchored = pattern.EndsWith('$');
        if (anchored)
        {
            pattern = pattern[..^1];
        }

        if (!pattern.Contains('*'))
        {
            return anchored
                ? path.Equals(pattern, StringComparison.Ordinal)
                : path.StartsWith(pattern, StringComparison.Ordinal);
        }

        // Walk the literal segments in order rather than building a regex: robots patterns are
        // simple, and this avoids handing user-controlled input to the regex engine.
        var segments = pattern.Split('*');
        var cursor = 0;

        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            if (segment.Length == 0)
            {
                continue;
            }

            if (i == 0)
            {
                if (!path.StartsWith(segment, StringComparison.Ordinal))
                {
                    return false;
                }

                cursor = segment.Length;
                continue;
            }

            var found = path.IndexOf(segment, cursor, StringComparison.Ordinal);
            if (found < 0)
            {
                return false;
            }

            cursor = found + segment.Length;
        }

        return !anchored || cursor == path.Length;
    }
}
