using DataIntelligence.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace DataIntelligence.IntegrationTests.Collection;

/// <summary>
/// Creates a real SQL Server database from the EF migrations for the duration of the test
/// class, then drops it.
/// </summary>
/// <remarks>
/// Deliberately a real database rather than the in-memory provider: the behaviour under test is
/// the collector-to-database flow (SOW 11.1), and computed columns, check constraints, unique
/// indexes and rowversion — the things that actually enforce FR-3 — do not exist in-memory.
/// <para>
/// Defaults to a local default instance; set <c>DATAINTELLIGENCE_TEST_SQL</c> to point at
/// another server. Each run uses a uniquely named database, so concurrent runs and leftover
/// state from a previous run cannot interfere.
/// </para>
/// </remarks>
public sealed class CollectionDatabaseFixture : IAsyncLifetime
{
    private const string DefaultServer = "Server=localhost;Trusted_Connection=True;TrustServerCertificate=True;";

    public string DatabaseName { get; } = $"DI_IntegrationTests_{Guid.NewGuid():N}";

    public string ConnectionString { get; private set; } = string.Empty;

    public bool IsAvailable { get; private set; }

    /// <summary>Why the database could not be created, for the failure message.</summary>
    public string UnavailableReason { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        var serverConnectionString =
            Environment.GetEnvironmentVariable("DATAINTELLIGENCE_TEST_SQL") ?? DefaultServer;

        var builder = new SqlConnectionStringBuilder(serverConnectionString)
        {
            InitialCatalog = "master"
        };

        try
        {
            await using var connection = new SqlConnection(builder.ConnectionString);
            await connection.OpenAsync();

            await using (var create = connection.CreateCommand())
            {
                create.CommandText = $"CREATE DATABASE [{DatabaseName}];";
                await create.ExecuteNonQueryAsync();
            }
        }
        catch (SqlException ex)
        {
            UnavailableReason =
                "Integration tests need a reachable SQL Server. Tried "
                + $"'{builder.DataSource}'. Set DATAINTELLIGENCE_TEST_SQL to point at another "
                + $"instance. Underlying error: {ex.Message}";
            return;
        }

        builder.InitialCatalog = DatabaseName;
        ConnectionString = builder.ConnectionString;

        // Migrate rather than EnsureCreated, so the tests exercise the same schema that deploys.
        await using var context = CreateContext();
        await context.Database.MigrateAsync();

        IsAvailable = true;
    }

    public DataIntelligenceDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<DataIntelligenceDbContext>()
            .UseSqlServer(ConnectionString)
            .Options;

        return new DataIntelligenceDbContext(options);
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
