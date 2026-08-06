using DataIntelligence.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace DataIntelligence.IntegrationTests.Ai;

/// <summary>
/// A real database carrying the least-privilege arrangement the assistant depends on: the
/// <c>analytics</c> read models, the <c>di_ai_readonly</c> role, and a <c>di_ai_user</c> principal
/// in it (SOW 9, FR-14).
/// </summary>
/// <remarks>
/// Built here rather than taken from the migrations because the migrations do not create it. The
/// views and the role live in <c>docs/database-schema.sql</c>, which is the design of record for
/// the read models; EF only owns <c>collect</c>, <c>core</c> and <c>ai</c>. The grants below are
/// the same ones section 6 of that script issues, reduced to the objects these tests touch.
/// <para>
/// This has to be a real SQL Server. The behaviour under test *is* the permission system — DENY,
/// role membership and EXECUTE AS have no in-memory equivalent, and a fake that approximated them
/// would be asserting its own approximation.
/// </para>
/// </remarks>
public sealed class ReadOnlyExecutionFixture : IAsyncLifetime
{
    private const string DefaultServer = "Server=localhost;Trusted_Connection=True;TrustServerCertificate=True;";

    public const string ReadOnlyUser = "di_ai_user";

    public string DatabaseName { get; } = $"DI_AiReadOnly_{Guid.NewGuid():N}";

    public string ConnectionString { get; private set; } = string.Empty;

    public bool IsAvailable { get; private set; }

    public string UnavailableReason { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        var server = Environment.GetEnvironmentVariable("DATAINTELLIGENCE_TEST_SQL") ?? DefaultServer;

        var builder = new SqlConnectionStringBuilder(server) { InitialCatalog = "master" };

        try
        {
            await using var connection = new SqlConnection(builder.ConnectionString);
            await connection.OpenAsync();

            await using var create = connection.CreateCommand();
            create.CommandText = $"CREATE DATABASE [{DatabaseName}];";
            await create.ExecuteNonQueryAsync();
        }
        catch (SqlException ex)
        {
            UnavailableReason =
                $"Integration tests need a reachable SQL Server. Tried '{builder.DataSource}'. "
                + $"Set DATAINTELLIGENCE_TEST_SQL to point at another instance. Error: {ex.Message}";
            return;
        }

        builder.InitialCatalog = DatabaseName;
        ConnectionString = builder.ConnectionString;

        await using (var context = CreateContext())
        {
            await context.Database.MigrateAsync();
        }

        await CreateReadModelsAndRoleAsync();

        IsAvailable = true;
    }

    public DataIntelligenceDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<DataIntelligenceDbContext>()
            .UseSqlServer(ConnectionString)
            .Options);

    /// <summary>
    /// Mirrors section 5 and 6 of the schema script for the objects under test: an analytics view
    /// over the CPI table, a role that may read it and nothing else, and a user in that role.
    /// </summary>
    private async Task CreateReadModelsAndRoleAsync()
    {
        // QUOTED_IDENTIFIER must be on for the view; it is off by default over the raw driver.
        var batches = new[]
        {
            "SET QUOTED_IDENTIFIER ON;",

            "IF SCHEMA_ID('analytics') IS NULL EXEC('CREATE SCHEMA analytics');",

            """
            EXEC('CREATE VIEW analytics.vw_Cpi AS
                  SELECT ReferenceDate, ReferenceYear, PeriodCode, SeriesCode, IndexValue
                  FROM core.CpiObservation WHERE IsCurrent = 1');
            """,

            "IF DATABASE_PRINCIPAL_ID('di_ai_readonly') IS NULL CREATE ROLE di_ai_readonly;",

            "GRANT SELECT ON analytics.vw_Cpi TO di_ai_readonly;",

            // The two schemas the assistant must never reach: the audit trail it writes, and
            // identity. core is denied too — the views exist so the base tables need not be read.
            "DENY SELECT, INSERT, UPDATE, DELETE ON SCHEMA::core TO di_ai_readonly;",
            "DENY SELECT, INSERT, UPDATE, DELETE ON SCHEMA::ai   TO di_ai_readonly;",

            $"""
             IF DATABASE_PRINCIPAL_ID('{ReadOnlyUser}') IS NULL
             BEGIN
                 CREATE USER {ReadOnlyUser} WITHOUT LOGIN;
                 ALTER ROLE di_ai_readonly ADD MEMBER {ReadOnlyUser};
             END
             """
        };

        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();

        foreach (var batch in batches)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = batch;
            await command.ExecuteNonQueryAsync();
        }
    }

    public async Task DisposeAsync()
    {
        if (!IsAvailable)
        {
            return;
        }

        var builder = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" };

        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync();

        await using var drop = connection.CreateCommand();
        drop.CommandText =
            $"ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; "
            + $"DROP DATABASE [{DatabaseName}];";

        await drop.ExecuteNonQueryAsync();
    }
}
