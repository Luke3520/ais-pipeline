using System.Globalization;
using AisPipeline.Core.Domain;
using AisPipeline.Core.Parsing;

namespace AisPipeline.Tests.Parsing;

public class RawAisRecordParserTests
{
    /// <summary>A real row from aisdk-2026-09-05, line 2.</summary>
    private const string RealRow =
        "05/09/2026 00:00:00,Class A,219005866,57.321455,11.126527,Unknown value,0.0,0.0,212.1," +
        "121,Unknown,Unknown,,Undefined,,,,Undefined,,Unknown,,AIS,,,,";

    private static AisSourceLine Line(string csv, long number = 2) =>
        new("aisdk-2026-09-05.csv", number, csv, csv.Split(','));

    [Fact]
    public void ParsesARealRow()
    {
        var result = RawAisRecordParser.Parse(Line(RealRow));

        Assert.True(result.Ok);
        var record = result.Record!;
        Assert.Equal(219005866, record.Mmsi);
        Assert.Equal(57.321455, record.Latitude);
        Assert.Equal(11.126527, record.Longitude);
        Assert.Equal(0.0, record.SpeedOverGroundKn);
        Assert.Equal(121.0, record.HeadingDegrees);
    }

    [Fact]
    public void TimestampIsUtcNotLocal()
    {
        // AdjustToUniversal|AssumeUniversal. Without both, a machine in CEST shifts every
        // timestamp by two hours and every derived duration silently with it.
        var record = RawAisRecordParser.Parse(Line(RealRow)).Record!;

        Assert.Equal(DateTimeKind.Utc, record.TimestampUtc.Kind);
        Assert.Equal(new DateTime(2026, 9, 5, 0, 0, 0, DateTimeKind.Utc), record.TimestampUtc);
    }

    [Fact]
    public void DayAndMonthAreNotTransposed()
    {
        // 05/09 is 5 September, not 9 May. A row on the 13th of a month would throw on the
        // American reading, so the ambiguous dates are exactly the ones that parse silently wrong.
        var record = RawAisRecordParser.Parse(Line(RealRow)).Record!;

        Assert.Equal(9, record.TimestampUtc.Month);
        Assert.Equal(5, record.TimestampUtc.Day);
    }

    [Fact]
    public void GlobalizationIsInvariantSoAmbientCultureCannotReachTheParser()
    {
        // InvariantGlobalization is set solution-wide, which means no culture other than the
        // invariant one exists at runtime. That is a stronger guarantee than passing
        // InvariantCulture at each call site -- the ambient culture cannot differ in the first
        // place -- and it matters because this project is developed on a Danish locale where
        // ',' is the decimal separator. If this assertion ever fails, InvariantGlobalization
        // has been removed and every numeric parse needs re-auditing.
        Assert.Throws<CultureNotFoundException>(() => new CultureInfo("da-DK"));
    }

    [Fact]
    public void CommaDecimalLatitudeIsRefusedRatherThanMisread()
    {
        // DMA's own published column documentation shows latitude as "57,8794" while the real
        // files use "57.321455". If the producer ever switched to the documented form, this
        // must fail loudly: under InvariantCulture "57,321455" does not parse, so the row is
        // rejected by R1 rather than silently read as some other number.
        var fields = RealRow.Split(',');
        fields[DmaColumns.Latitude] = "57,321455";
        var result = RawAisRecordParser.Parse(
            new AisSourceLine("f.csv", 2, string.Join(';', fields), fields));

        Assert.False(result.Ok);
    }

    [Theory]
    [InlineData(25)]
    [InlineData(27)]
    public void WrongFieldCountFails(int fieldCount)
    {
        var fields = Enumerable.Repeat("x", fieldCount).ToArray();
        var result = RawAisRecordParser.Parse(
            new AisSourceLine("f.csv", 2, string.Join(',', fields), fields));

        Assert.False(result.Ok);
        Assert.Contains("26 fields", result.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public void ExactlyTwentySixFieldsIsAccepted()
    {
        // The boundary on the passing side of the field-count guard.
        Assert.True(RawAisRecordParser.Parse(Line(RealRow)).Ok);
        Assert.Equal(26, RealRow.Split(',').Length);
    }

    [Fact]
    public void UnparseableTimestampFails()
    {
        var result = RawAisRecordParser.Parse(Line(RealRow.Replace(
            "05/09/2026 00:00:00", "2026-09-05T00:00:00", StringComparison.Ordinal)));

        Assert.False(result.Ok);
        Assert.Contains("timestamp", result.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public void TextualUnknownsBecomeNullNotTheLiteralString()
    {
        // DMA writes "Unknown" and "Undefined" rather than leaving fields blank. A
        // null-or-empty check misses every one: IMO reads "Unknown" on 47% of feed rows.
        var record = RawAisRecordParser.Parse(Line(RealRow)).Record!;

        Assert.Null(record.Imo);
        Assert.Null(record.CallSign);
        Assert.Null(record.ShipType);
    }

    [Fact]
    public void BlankOptionalNumericsBecomeNullNotZero()
    {
        // A blank SOG stored as 0.0 would read as "stationary" and fabricate stop events.
        var fields = RealRow.Split(',');
        fields[DmaColumns.SpeedOverGround] = string.Empty;
        var record = RawAisRecordParser.Parse(
            new AisSourceLine("f.csv", 2, string.Join(',', fields), fields)).Record!;

        Assert.Null(record.SpeedOverGroundKn);
    }

    [Fact]
    public void ProvenanceSurvivesParsing()
    {
        var record = RawAisRecordParser.Parse(Line(RealRow, 4242)).Record!;

        Assert.Equal(4242, record.SourceLine);
        Assert.Equal("aisdk-2026-09-05.csv", record.SourceFile);
        Assert.Equal(RealRow, record.RawText);
    }
}
