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

    /// <summary>
    /// Engine selection and connection opening live in <see cref="ReadConnection"/>, shared with
    /// the CLI. Two copies drifted within a day of the second one existing -- one opened SQLite
    /// read-only and the other did not.
    /// </summary>
    private ReadConnection Connection => ReadConnection.Resolve(Postgres, SqlitePath);

    public SqlDialect Dialect => Connection.Dialect;

    public Func<DbConnection> ConnectionFactory => Connection.Factory;

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
