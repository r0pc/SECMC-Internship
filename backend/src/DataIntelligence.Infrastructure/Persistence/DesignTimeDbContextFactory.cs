using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DataIntelligence.Infrastructure.Persistence;

/// <summary>
/// Lets <c>dotnet ef</c> build the model without starting a host or reaching a database.
/// </summary>
/// <remarks>
/// Without this, generating a migration would need a real connection string on the developer's
/// machine — which is exactly what is kept out of source control (SOW 3 — Security). The
/// placeholder below is never connected to; EF only needs the provider to translate the model.
/// </remarks>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<DataIntelligenceDbContext>
{
    /// <summary>Environment variable checked first, matching ASP.NET Core's own naming.</summary>
    public const string ConnectionStringVariable = "ConnectionStrings__DataIntelligenceDb";

    /// <summary>
    /// Not a reachable server. Enough for EF to translate the model when only a migration is
    /// being generated, and it fails loudly rather than silently targeting the wrong database.
    /// </summary>
    private const string ModelOnlyPlaceholder =
        "Server=(design-time);Database=DataIntelligence;Trusted_Connection=True;";

    public DataIntelligenceDbContext CreateDbContext(string[] args)
    {
        // Applying a migration needs a real server; generating one does not. Reading the
        // variable here means `dotnet ef database update` works without a startup project,
        // and the credentials still never touch source control.
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);

        var options = new DbContextOptionsBuilder<DataIntelligenceDbContext>()
            .UseSqlServer(string.IsNullOrWhiteSpace(connectionString)
                ? ModelOnlyPlaceholder
                : connectionString)
            .Options;

        return new DataIntelligenceDbContext(options);
    }
}
