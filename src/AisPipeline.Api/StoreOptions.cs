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

    public static StoreOptions FromConfiguration(IConfiguration configuration)
    {
        var postgres = configuration["AIS_POSTGRES"] ?? configuration.GetConnectionString("Postgres");

        return new StoreOptions
        {
            // Empty is not the same as absent. A caller setting AIS_POSTGRES="" is explicitly
            // asking for SQLite, which is how a test pins itself to the database it built rather
            // than inheriting whatever the ambient environment happens to point at.
            Postgres = string.IsNullOrWhiteSpace(postgres) ? null : postgres,
            SqlitePath = configuration["AIS_SQLITE"] ?? IoPath.Combine("data", "ais.db"),
        };
    }
}
