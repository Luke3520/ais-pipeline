using AisPipeline.Adapters.Postgres;
using AisPipeline.Core.Ports;
using Npgsql;

namespace AisPipeline.IntegrationTests.Harness;

/// <summary>
/// A throwaway Postgres schema per test, inside the database named by AIS_POSTGRES.
///
/// A schema rather than a database: it is fast enough to do per test, and it isolates tests from
/// each other without the connection dance a CREATE DATABASE requires.
/// </summary>
public sealed class PostgresHarness : IStoreHarness
{
    /// <summary>Connection string for the test database, e.g. from compose.yaml.</summary>
    public const string EnvironmentVariable = "AIS_POSTGRES";

    private readonly string _schema = $"t{Guid.NewGuid():N}";
    private readonly string _baseConnectionString;

    public PostgresHarness(string baseConnectionString)
    {
        _baseConnectionString = baseConnectionString;

        using var connection = new NpgsqlConnection(baseConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"CREATE SCHEMA \"{_schema}\";";
        command.ExecuteNonQuery();
    }

    /// <summary>Configured connection string, or null when Postgres is not available here.</summary>
    public static string? ConnectionString =>
        Environment.GetEnvironmentVariable(EnvironmentVariable);

    public static bool IsAvailable => !string.IsNullOrWhiteSpace(ConnectionString);

    public string Name => "Postgres";

    private string ScopedConnectionString =>
        new NpgsqlConnectionStringBuilder(_baseConnectionString) { SearchPath = _schema }.ToString();

    public IAisStore Create() => new PostgresAisStore(ScopedConnectionString);

    public long Count(string table) => Scalar($"SELECT COUNT(*) FROM {table}");

    public long CountWhere(string sql) => Scalar(sql);

    long IStoreHarness.Scalar(string sql) => Scalar(sql);

    private long Scalar(string sql)
    {
        using var connection = new NpgsqlConnection(ScopedConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar()!);
    }

    public void Dispose()
    {
        using var connection = new NpgsqlConnection(_baseConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"DROP SCHEMA IF EXISTS \"{_schema}\" CASCADE;";
        command.ExecuteNonQuery();
        NpgsqlConnection.ClearAllPools();
    }
}
