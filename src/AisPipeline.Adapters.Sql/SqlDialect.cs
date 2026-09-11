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
