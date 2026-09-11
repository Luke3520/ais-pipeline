using System.Data.Common;
using AisPipeline.Adapters.Sql;
using AisPipeline.Adapters.Sqlite;
using AisPipeline.Core.Ports;
using Microsoft.Data.Sqlite;

namespace AisPipeline.IntegrationTests.Harness;

/// <summary>A throwaway SQLite file per test.</summary>
public sealed class SqliteHarness : IStoreHarness
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"ais-test-{Guid.NewGuid():N}.db");

    public string Name => "SQLite";

    public IAisStore Create() => new SqliteAisStore(_path);

    public Func<DbConnection> ConnectionFactory =>
        () => new SqliteConnection($"Data Source={_path}");

    public SqlDialect Dialect => SqlDialect.Sqlite;

    public long Count(string table) => Scalar($"SELECT COUNT(*) FROM {table}");

    public long CountWhere(string sql) => Scalar(sql);

    long IStoreHarness.Scalar(string sql) => Scalar(sql);

    private long Scalar(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={_path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            File.Delete(_path + suffix);
        }
    }
}
