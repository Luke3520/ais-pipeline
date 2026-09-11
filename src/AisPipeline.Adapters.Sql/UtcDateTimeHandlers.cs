using System.Data;
using System.Globalization;
using Dapper;

namespace AisPipeline.Adapters.Sql;

/// <summary>
/// Reads a timestamp back as UTC whatever the provider hands over.
///
/// SQLite has no date type and returns the stored ISO string; Postgres returns a DateTime. Left to
/// its own devices Dapper would convert the string with <c>Convert.ChangeType</c>, which parses it
/// into a <see cref="DateTimeKind.Local"/> value — silently shifting every instant by the machine's
/// offset. On a Danish laptop in summer that is two hours on every stop boundary, and nothing would
/// throw.
///
/// The pipeline is UTC end to end (docs/rules/units-and-geodesy.md), so this pins that at the one
/// boundary where the two engines disagree about what a timestamp even is.
/// </summary>
public sealed class UtcDateTimeHandler : SqlMapper.TypeHandler<DateTime>
{
    public static void Register()
    {
        SqlMapper.AddTypeHandler(new UtcDateTimeHandler());
        SqlMapper.AddTypeHandler(new NullableUtcDateTimeHandler());
    }

    public override DateTime Parse(object value) => ToUtc(value);

    public override void SetValue(IDbDataParameter parameter, DateTime value) =>
        parameter.Value = value.ToUniversalTime();

    internal static DateTime ToUtc(object value) => value switch
    {
        DateTime { Kind: DateTimeKind.Utc } utc => utc,
        DateTime { Kind: DateTimeKind.Local } local => local.ToUniversalTime(),

        // Postgres returns Unspecified for a value read out of TIMESTAMPTZ in some paths. It is
        // already an instant in UTC; labelling it Local would move it.
        DateTime unspecified => DateTime.SpecifyKind(unspecified, DateTimeKind.Utc),

        string text => DateTime.ParseExact(
            text,
            StoredFormats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),

        _ => throw new InvalidCastException(
            $"cannot read a timestamp from {value?.GetType().Name ?? "null"}"),
    };

    /// <summary>
    /// The shapes a stored timestamp can take. The first is what this project writes; the others
    /// are what SQLite accepts from a database written by another tool, and failing loudly on an
    /// unrecognised shape beats guessing at one.
    /// </summary>
    private static readonly string[] StoredFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ss'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd HH:mm:ss.FFFFFFF",
    ];
}

/// <summary>The same, for columns that may be absent.</summary>
public sealed class NullableUtcDateTimeHandler : SqlMapper.TypeHandler<DateTime?>
{
    public override DateTime? Parse(object value) =>
        value is null or DBNull ? null : UtcDateTimeHandler.ToUtc(value);

    public override void SetValue(IDbDataParameter parameter, DateTime? value) =>
        parameter.Value = value?.ToUniversalTime() ?? (object)DBNull.Value;
}
