using DataIntelligence.Core.Entities;
using DataIntelligence.Core.Interfaces;

namespace DataIntelligence.UnitTests.Collection;

/// <summary>Builds an adapter request context with sensible defaults for tests.</summary>
internal sealed class SourceRequestContextBuilder
{
    private readonly List<string> _seriesCodes = [];
    private DateTime _utcNow = new(2026, 8, 4, 10, 0, 0, DateTimeKind.Utc);

    public SourceRequestContextBuilder WithSeries(params string[] codes)
    {
        _seriesCodes.AddRange(codes);
        return this;
    }

    public SourceRequestContextBuilder At(DateTime utcNow)
    {
        _utcNow = utcNow;
        return this;
    }

    public SourceRequestContext Build() => new(
        new DataSource
        {
            DataSourceId = 1,
            Code = "TEST",
            ApiEndpoint = "https://example.test/api",
            RequestTimeoutSec = 30,
            MaxRetries = 3
        },
        _seriesCodes,
        _utcNow);
}
