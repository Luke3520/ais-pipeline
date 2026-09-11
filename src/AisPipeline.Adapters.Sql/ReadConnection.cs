using System.Data.Common;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace AisPipeline.Adapters.Sql;

/// <summary>
/// Chooses the engine for a read-only consumer and opens connections to it.
///
/// One home, because the CLI and the API both do this and had already drifted apart within a day
/// of the second copy existing: one opened SQLite with <c>Mode=ReadOnly</c> and the other did not,
/// on a path that only reads.
///
/// Read-only is not decoration. Both consumers are query surfaces, and SQLite will happily create
/// an empty database file at a mistyped path and then report zero rows -- an answer that looks like
/// data rather than like an error.
/// </summary>
public sealed record ReadConnection
{
    /// <summary>Postgres connection string. Takes precedence over <see cref="SqlitePath"/>.</summary>
    public string? Postgres { get; init; }

    public string SqlitePath { get; init; } = Path.Combine("data", "ais.db");

    public SqlDialect Dialect => Postgres is null ? SqlDialect.Sqlite : SqlDialect.Postgres;

    public Func<DbConnection> Factory => Postgres is { } connectionString
        ? () => new NpgsqlConnection(connectionString)
        : () => new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = SqlitePath,
                Mode = SqliteOpenMode.ReadOnly,
            }.ToString());

    /// <summary>
    /// Resolve from a Postgres connection string and a SQLite path, either of which may be absent.
    /// Empty is treated as absent: a caller passing an empty Postgres string is explicitly asking
    /// for SQLite.
    /// </summary>
    public static ReadConnection Resolve(string? postgres, string? sqlitePath) => new()
    {
        Postgres = string.IsNullOrWhiteSpace(postgres) ? null : postgres,
        SqlitePath = string.IsNullOrWhiteSpace(sqlitePath)
            ? Path.Combine("data", "ais.db")
            : sqlitePath,
    };

    public SqlAisQueries OpenQueries() => new(Factory, Dialect);
}
