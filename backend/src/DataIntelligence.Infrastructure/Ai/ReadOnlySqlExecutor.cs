// backend/src/DataIntelligence.Infrastructure/Ai/ReadOnlySqlExecutor.cs
using System.Text.RegularExpressions;
using DataIntelligence.Core.Exceptions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace DataIntelligence.Infrastructure.Ai;

/// <summary>
/// Runs a validated statement with the least privilege the platform can give it — never the app's
/// read-write context — so a statement that somehow passed validation still cannot write (SOW 9).
/// </summary>
/// <remarks>
/// Least privilege is applied two ways, and which one is available depends on the server:
/// <list type="number">
/// <item>A connection authenticating as a login in the <c>di_ai_readonly</c> role, configured as
/// <c>ConnectionStrings:DataIntelligenceDbReadOnly</c>. The strongest option, and unavailable on a
/// Windows-authentication-only instance, where the role has no login to authenticate as.</item>
/// <item><c>EXECUTE AS USER</c>, switching the session to a database user in that role for the
/// duration of the statement. Works under Windows authentication and needs no server
/// reconfiguration, which is why it is the default.</item>
/// </list>
/// Both end at the same place: the statement runs as a principal holding SELECT on
/// <c>analytics.*</c> and DENY on <c>sec</c> and <c>ai</c>. Neither replaces
/// <see cref="ISqlSafetyValidator"/> — this is the layer that holds when the validator is wrong.
/// </remarks>
public sealed class ReadOnlySqlExecutor
{
    public const string ConnectionStringName = "DataIntelligenceDbReadOnly";

    /// <summary>
    /// A database principal name, which cannot be parameterised — <c>EXECUTE AS USER</c> takes a
    /// literal. Restricting it to identifier characters is what makes embedding it safe; the value
    /// comes from configuration rather than from a caller, and this is the belt to that's braces.
    /// </summary>
    private static readonly Regex SafePrincipalName = new(@"^[A-Za-z_][A-Za-z0-9_]{0,127}$", RegexOptions.Compiled);

    private readonly string? _readOnlyConnectionString;
    private readonly string _appConnectionString;
    private readonly AssistantOptions _options;

    public ReadOnlySqlExecutor(IConfiguration configuration, IOptions<AssistantOptions> options)
    {
        _readOnlyConnectionString = configuration.GetConnectionString(ConnectionStringName);
        _appConnectionString = configuration.GetConnectionString(DependencyInjection.ConnectionStringName) ?? string.Empty;
        _options = options.Value;
    }

    public async Task<QueryExecutionResult> ExecuteAsync(
        string sql,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken cancellationToken)
    {
        var (connectionString, impersonate) = ResolveConnection();

        await using var connection = new SqlConnection(connectionString);

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_options.SqlExecutionTimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var impersonated = false;

        try
        {
            await connection.OpenAsync(linked.Token);

            if (impersonate is not null)
            {
                await ImpersonateAsync(connection, impersonate, linked.Token);
                impersonated = true;
            }

            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = _options.SqlExecutionTimeoutSeconds;

            if (parameters is not null)
            {
                foreach (var (name, value) in parameters)
                {
                    command.Parameters.AddWithValue(
                        name.StartsWith('@') ? name : "@" + name, value ?? DBNull.Value);
                }
            }

            await using var reader = await command.ExecuteReaderAsync(linked.Token);

            var rows = new List<IReadOnlyDictionary<string, object?>>();

            // Second row cap, independent of the TOP the validator injects. An injected TOP binds
            // to one SELECT, so a UNION would otherwise return MaxRows *per branch*; stopping the
            // reader is the cap that holds whatever shape the statement turned out to be.
            while (rows.Count < SqlSafetyValidator.MaxRows && await reader.ReadAsync(linked.Token))
            {
                var row = new Dictionary<string, object?>(reader.FieldCount);
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }
                rows.Add(row);
            }

            return QueryExecutionResult.Success(rows);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            return QueryExecutionResult.Timeout($"Query exceeded {_options.SqlExecutionTimeoutSeconds}s.");
        }
        catch (SqlException ex)
        {
            // A statement that reaches SQL Server as the read-only principal and still fails is
            // either a permissions boundary doing its job, or a query the validator should have
            // caught — either way a Failed execution, not an exception thrown up the stack.
            return QueryExecutionResult.Failed(ex.Message);
        }
        finally
        {
            if (impersonated)
            {
                await RevertAsync(connection);
            }
        }
    }

    /// <summary>
    /// Picks the strongest privilege separation the configuration offers, and refuses to run
    /// without any.
    /// </summary>
    private (string ConnectionString, string? ImpersonateUser) ResolveConnection()
    {
        var dedicated = !string.IsNullOrWhiteSpace(_readOnlyConnectionString);
        var user = _options.ExecuteAsUser?.Trim();
        var impersonating = !string.IsNullOrWhiteSpace(user);

        if (!dedicated && !impersonating)
        {
            // Refused rather than silently falling back to the app's read-write connection. A
            // fallback here would leave the assistant executing model-written SQL with INSERT and
            // UPDATE rights, and nothing in the response would say so.
            throw new AssistantNotConfiguredException(
                $"The assistant has no read-only execution path. Configure either "
                + $"'ConnectionStrings:{ConnectionStringName}' (a login in the di_ai_readonly role) "
                + $"or '{AssistantOptions.SectionName}:{nameof(AssistantOptions.ExecuteAsUser)}' "
                + "(a database user in that role, used with EXECUTE AS). Running generated SQL on "
                + "the application's own connection is not an option.");
        }

        if (impersonating && !SafePrincipalName.IsMatch(user!))
        {
            throw new AssistantNotConfiguredException(
                $"'{AssistantOptions.SectionName}:{nameof(AssistantOptions.ExecuteAsUser)}' must be a "
                + $"plain SQL identifier; '{user}' is not.");
        }

        // A dedicated login already carries the restricted rights, so impersonating on top of it
        // would be redundant — and would fail unless that login could also impersonate.
        return dedicated
            ? (_readOnlyConnectionString!, null)
            : (_appConnectionString, user);
    }

    private static async Task ImpersonateAsync(
        SqlConnection connection, string user, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"EXECUTE AS USER = '{user.Replace("'", "''")}'";

        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqlException ex)
        {
            throw new AssistantNotConfiguredException(
                $"Could not switch to database user '{user}' before running generated SQL "
                + $"({ex.Message}). It must exist in this database and belong to the "
                + "di_ai_readonly role. Nothing was executed.");
        }
    }

    /// <summary>
    /// Drops the impersonated context before the connection returns to the pool.
    /// </summary>
    /// <remarks>
    /// <c>sp_reset_connection</c> resets the security context when a pooled connection is reused,
    /// so this is belt and braces — but leaking an impersonated session into the pool would be a
    /// privilege bug found much later and somewhere else, and REVERT costs one round trip.
    /// Failures are swallowed: the connection is being torn down regardless, and throwing here
    /// would replace a real result, or a real error, with this one.
    /// </remarks>
    private static async Task RevertAsync(SqlConnection connection)
    {
        try
        {
            if (connection.State != System.Data.ConnectionState.Open)
            {
                return;
            }

            await using var command = connection.CreateCommand();
            command.CommandText = "REVERT";
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
        catch (SqlException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }
}

public sealed record QueryExecutionResult(
    bool Succeeded,
    bool TimedOut,
    IReadOnlyList<IReadOnlyDictionary<string, object?>>? Rows,
    string? ErrorMessage)
{
    public static QueryExecutionResult Success(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows) =>
        new(true, false, rows, null);

    public static QueryExecutionResult Failed(string message) => new(false, false, null, message);

    public static QueryExecutionResult Timeout(string message) => new(false, true, null, message);
}
