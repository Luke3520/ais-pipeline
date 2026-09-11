using System.Data.Common;
using IoPath = System.IO.Path;
using AisPipeline.Adapters.Sql;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace AisPipeline.Api;

/// <summary>Which database the API reads, resolved once at startup.</summary>
public sealed class StoreOptions
{
    /// <summary>Postgres connection string. Takes precedence over <see cref="SqlitePath"/>.</summary>
    public string? Postgres { get; init; }

    public string SqlitePath { get; init; } = IoPath.Combine("data", "ais.db");

    public SqlDialect Dialect => Postgres is null ? SqlDialect.Sqlite : SqlDialect.Postgres;

    public Func<DbConnection> ConnectionFactory => Postgres is { } connectionString
        ? () => new NpgsqlConnection(connectionString)
        : () => new SqliteConnection($"Data Source={SqlitePath};Mode=ReadOnly");

    public static StoreOptions FromConfiguration(IConfiguration configuration) => new()
    {
        // AIS_POSTGRES is the same variable the test suite and compose.yaml use.
        Postgres = configuration["AIS_POSTGRES"] ?? configuration.GetConnectionString("Postgres"),
        SqlitePath = configuration["AIS_SQLITE"] ?? IoPath.Combine("data", "ais.db"),
    };
}
