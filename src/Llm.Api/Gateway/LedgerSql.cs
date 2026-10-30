using Llm.Api.Dashboards;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Llm.Api.Gateway;

/// <summary>
/// SQL over the app's own ledger of model requests (gateway.requests, in the app's database), read only and within the
/// dashboards' statement timeout, its answer as the dashboards' SQL gives one (the pages shape it the same way).
/// </summary>
public sealed class LedgerSql(IConfiguration config, IOptions<DashboardOptions> options)
{
    private readonly string _connectionString = new NpgsqlConnectionStringBuilder(DatabaseSettings.ConnectionString(config))
    {
        ApplicationName = "llm-app ledger",
    }.ConnectionString;

    public async Task<TableResult> QueryAsync(string sql, IReadOnlyDictionary<string, object> parameters, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var setup = new NpgsqlCommand($"SET TRANSACTION READ ONLY; SET LOCAL statement_timeout = {(int)options.Value.StatementTimeout.TotalMilliseconds}", conn, tx))
        {
            await setup.ExecuteNonQueryAsync(ct);
        }
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var columns = Enumerable.Range(0, reader.FieldCount).Select(i => new Column(reader.GetName(i), SqlDatasource.TypeOf(reader.GetFieldType(i)))).ToList();
        var rows = new List<object?[]>();
        while (await reader.ReadAsync(ct))
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++)
            {
                row[i] = reader.IsDBNull(i) ? null : SqlDatasource.Normalise(reader.GetValue(i));
            }
            rows.Add(row);
        }
        return new TableResult(columns, rows, false);
    }
}
