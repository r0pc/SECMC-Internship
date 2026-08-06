// backend/src/DataIntelligence.Infrastructure/Ai/ReadOnlySqlExecutor.cs
using Microsoft.Data.SqlClient;

namespace DataIntelligence.Infrastructure.Ai;

/// <summary>
/// Runs a validated statement over the <c>di_ai_readonly</c> login's own connection — never the
/// app's read-write context — so a statement that somehow passed validation still cannot write.
/// </summary>
public sealed class ReadOnlySqlExecutor
{
    private readonly string _connectionString;
    private readonly AssistantOptions _options;

    public ReadOnlySqlExecutor(IConfiguration configuration, IOptions<AssistantOptions> options)
    {
        _connectionString = configuration.GetConnectionString("DataIntelligenceDbReadOnly")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:DataIntelligenceDbReadOnly is not configured. It must authenticate "
                + "as di_ai_readonly, which can SELECT from analytics.* only.");
        _options = options.Value;
    }

    public async Task<QueryExecutionResult> ExecuteAsync(string sql, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_options.SqlExecutionTimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            await connection.OpenAsync(linked.Token);

            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = _options.SqlExecutionTimeoutSeconds;

            await using var reader = await command.ExecuteReaderAsync(linked.Token);

            var rows = new List<IReadOnlyDictionary<string, object?>>();

            while (await reader.ReadAsync(linked.Token))
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
            // A statement that reaches SQL Server as di_ai_readonly and still fails is either a
            // permissions boundary doing its job, or a query the validator should have caught —
            // either way it is a Failed execution, not a thrown exception up the stack.
            return QueryExecutionResult.Failed(ex.Message);
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