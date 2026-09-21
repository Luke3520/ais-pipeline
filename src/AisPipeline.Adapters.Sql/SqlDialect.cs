using System.Globalization;

namespace AisPipeline.Adapters.Sql;

/// <summary>
/// The one place the read queries cannot be spelled identically.
///
/// Everything else in <see cref="SqlAisQueries"/> is plain ANSI SELECT that both engines accept
/// unchanged. Booleans are not: SQLite has no boolean type and stores 0/1, so a predicate reads
/// <c>is_complete = 1</c>; Postgres has one and rejects that outright with
/// <c>operator does not exist: boolean = integer</c>.
///
/// That is a difference in SQL semantics rather than syntax, which is why Dapper cannot smooth it
/// over the way it smooths over named parameters. One knob, named, beats either a second
/// implementation or storing booleans as integers in Postgres and giving up the type.
/// </summary>
public enum SqlDialect
{
    Sqlite,
    Postgres,
}

internal static class DialectExtensions
{
    /// <summary>Predicate that is true when the column is true.</summary>
    public static string IsTrue(this SqlDialect dialect, string column) =>
        dialect == SqlDialect.Postgres ? column : $"{column} = 1";

    /// <summary>Predicate that is true when the column is false.</summary>
    public static string IsFalse(this SqlDialect dialect, string column) =>
        dialect == SqlDialect.Postgres ? $"NOT {column}" : $"{column} = 0";

    /// <summary>
    /// A timestamp as a comparable parameter for this engine.
    ///
    /// SQLite has no date type: the column holds the ISO string this project writes, and comparing
    /// it is a TEXT comparison. A DateTime parameter binds as "2026-09-05 00:00:00" where the
    /// column reads "2026-09-05T00:00:00Z", so the comparison is decided by ' ' sorting below 'T'
    /// rather than by the instant -- and a bounded window silently returned nothing. It did not
    /// throw, and every existing test passed a window of UnixEpoch to MaxValue, which is the one
    /// shape the skew cannot affect.
    ///
    /// Postgres keeps an instant in TIMESTAMPTZ and must get the DateTime itself; formatting it
    /// would be the same mistake pointed the other way.
    ///
    /// The format matches what the SQLite store writes. Keep the two in step -- see
    /// SqliteAisStore.TimestampFormat.
    /// </summary>
    public static object Timestamp(this SqlDialect dialect, DateTime value)
    {
        var utc = UtcDateTimeHandler.ToUtc(value);

        return dialect == SqlDialect.Postgres
            ? utc
            : utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Whole days from one timestamp column to another, truncated toward zero.
    ///
    /// SQLite has no interval type and reaches for julianday; Postgres subtracts the timestamps
    /// and needs the seconds out of the interval. TRUNC on both sides rather than FLOOR, because
    /// SQLite's CAST(... AS INTEGER) truncates toward zero and FLOOR would disagree with it for
    /// an ETA that has already passed -- which is exactly the population this measures.
    /// </summary>
    public static string DaysBetween(this SqlDialect dialect, string from, string to) =>
        dialect == SqlDialect.Postgres
            ? $"TRUNC(EXTRACT(EPOCH FROM ({to} - {from})) / 86400)::int"
            : $"CAST(julianday({to}) - julianday({from}) AS INTEGER)";

    /// <summary>
    /// Predicate matching a column against a collection of values.
    ///
    /// Dapper expands <c>IN @ids</c> into <c>IN (@ids1, @ids2, ...)</c>, which SQLite accepts and
    /// Npgsql does not survive -- it rewrites named parameters positionally and the server rejects
    /// the result with a syntax error.
    ///
    /// Postgres's own <c>= ANY(@ids)</c> is the better form anyway: the SQL text is identical
    /// whatever the batch size, so one prepared plan is reused, where an expanded IN list produces
    /// a distinct statement per length and churns the plan cache. That matters here because these
    /// are exactly the batched lookups DataLoader calls with a different count every time.
    /// </summary>
    public static string InList(this SqlDialect dialect, string column, string parameter) =>
        dialect == SqlDialect.Postgres ? $"{column} = ANY({parameter})" : $"{column} IN {parameter}";
}
