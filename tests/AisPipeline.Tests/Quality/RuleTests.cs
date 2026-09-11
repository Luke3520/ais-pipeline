using AisPipeline.Core.Domain;
using AisPipeline.Core.Parsing;
using AisPipeline.Core.Quality;
using AisPipeline.Core.Quality.Rules;

namespace AisPipeline.Tests.Quality;

/// <summary>
/// Boundary coverage, not branch coverage. Every comparison operator in a rule has the exact
/// value on each side of it pinned by an assertion, because that is the only value where an
/// off-by-one shows -- see docs/rules/checks-and-review.md.
/// </summary>
public class RuleTests
{
    private static RawAisRecord Record(
        double lat = 56.0,
        double lon = 10.0,
        double? sog = 5.0) => new()
        {
            SourceFile = "f.csv",
            SourceLine = 2,
            RawText = "raw",
            TimestampUtc = new DateTime(2026, 9, 5, 0, 0, 0, DateTimeKind.Utc),
            TypeOfMobile = "Class A",
            Mmsi = 219005866,
            Latitude = lat,
            Longitude = lon,
            NavigationalStatus = "Under way using engine",
            SpeedOverGroundKn = sog,
        };

    // ---- R4: position sentinel, null island, range ----

    [Fact]
    public void R4RejectsTheLatitudeSentinel()
    {
        // The sentinel is latitude 91 with longitude 0 -- not longitude 181, which the AIS
        // specification implies and which never appears in these files.
        var hit = new R4PositionSentinel().Evaluate(Record(lat: 91.0, lon: 0.0));

        Assert.NotNull(hit);
        Assert.Equal("R4", hit!.RuleId);
        Assert.Equal(RuleAction.Reject, hit.Action);
    }

    [Fact]
    public void R4RejectsLatitudeSentinelWhateverTheLongitude()
    {
        Assert.NotNull(new R4PositionSentinel().Evaluate(Record(lat: 91.0, lon: 11.5)));
    }

    [Fact]
    public void R4DoesNotFireOnLongitude181BecauseThatSentinelDoesNotExistHere()
    {
        // Longitude 181 is out of range and rejected for THAT reason, but the rule must not
        // treat it as the paired sentinel -- a lon == 181 test catches nothing in this feed.
        var hit = new R4PositionSentinel().Evaluate(Record(lat: 56.0, lon: 181.0));

        Assert.NotNull(hit);
        Assert.Contains("longitude", hit!.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void R4RejectsNullIsland() =>
        Assert.NotNull(new R4PositionSentinel().Evaluate(Record(lat: 0.0, lon: 0.0)));

    [Fact]
    public void R4AcceptsAZeroCoordinateThatIsNotNullIsland()
    {
        // Only 0,0 together is null island. Latitude 0 on the Greenwich meridian is a real
        // place, and rejecting either coordinate alone would discard valid positions.
        Assert.Null(new R4PositionSentinel().Evaluate(Record(lat: 0.0, lon: 10.0)));
        Assert.Null(new R4PositionSentinel().Evaluate(Record(lat: 56.0, lon: 0.0)));
    }

    [Theory]
    [InlineData(90.0, true)]    // boundary: the pole is valid
    [InlineData(-90.0, true)]
    [InlineData(90.0001, false)]
    [InlineData(-90.0001, false)]
    public void R4LatitudeBoundary(double latitude, bool accepted) =>
        Assert.Equal(accepted, new R4PositionSentinel().Evaluate(Record(lat: latitude)) is null);

    [Theory]
    [InlineData(180.0, true)]   // boundary: the antimeridian is valid
    [InlineData(-180.0, true)]
    [InlineData(180.0001, false)]
    [InlineData(-180.0001, false)]
    public void R4LongitudeBoundary(double longitude, bool accepted) =>
        Assert.Equal(accepted, new R4PositionSentinel().Evaluate(Record(lon: longitude)) is null);

    // ---- R5: unavailable speed ----

    [Fact]
    public void R5FlagsUnavailableSpeedRatherThanRejectingIt()
    {
        // The row is still a valid, located observation. Rejecting it would punch a hole in the
        // vessel's track for a reason unrelated to where the vessel was.
        var hit = new R5SpeedUnavailable().Evaluate(Record(sog: null));

        Assert.NotNull(hit);
        Assert.Equal(RuleAction.Flag, hit!.Action);
    }

    [Fact]
    public void R5DoesNotFireOnZeroSpeed()
    {
        // Zero is a reported speed -- a moored vessel. Confusing it with "not reported" is
        // exactly the conflation that would fabricate stops.
        Assert.Null(new R5SpeedUnavailable().Evaluate(Record(sog: 0.0)));
    }

    // ---- R6: implausible speed ----

    [Theory]
    [InlineData(29.9, null)]                 // below the flag boundary
    [InlineData(30.0, RuleAction.Flag)]      // boundary: flagged at exactly 30
    [InlineData(40.0, RuleAction.Flag)]      // boundary: 40 is flagged, not rejected
    [InlineData(40.0001, RuleAction.Reject)] // first value above the reject boundary
    [InlineData(102.3, RuleAction.Reject)]
    public void R6SpeedBoundaries(double sog, RuleAction? expected)
    {
        var hit = new R6ImplausibleSpeed().Evaluate(Record(sog: sog));

        Assert.Equal(expected, hit?.Action);
    }

    [Fact]
    public void R6IgnoresUnavailableSpeedBecauseThatIsR5sBusiness()
    {
        // Two rules firing on the same absence would double-count it in the quality report.
        Assert.Null(new R6ImplausibleSpeed().Evaluate(Record(sog: null)));
    }

    // ---- R1 and the registry ----

    [Fact]
    public void R1RejectsAnUnparseableLineAndCarriesTheReason()
    {
        var line = new AisSourceLine("f.csv", 7, "a,b,c", ["a", "b", "c"]);
        var hit = new R1UnparseableRow().Evaluate(line, RawAisRecordParser.Parse(line));

        Assert.NotNull(hit);
        Assert.Equal(RuleAction.Reject, hit!.Action);
        Assert.Contains("26 fields", hit.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryRegisteredRuleHasAUniqueIdAndADescription()
    {
        var rules = RuleRegistry.Default().All.ToList();

        Assert.NotEmpty(rules);
        Assert.Equal(rules.Count, rules.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(rules, r => Assert.False(string.IsNullOrWhiteSpace(r.Description)));
    }

    [Fact]
    public void JudgementCollectsEveryRuleThatFiredNotJustTheDecisiveOne()
    {
        // A rejected row can still carry flags, and the quality report counts every rule that
        // fired. Stopping at the first rejection would under-report every later rule.
        var fields = new string[DmaColumns.Count];
        Array.Fill(fields, string.Empty);
        fields[DmaColumns.Timestamp] = "05/09/2026 00:00:00";
        fields[DmaColumns.Mmsi] = "219005866";
        fields[DmaColumns.Latitude] = "91.000000";  // R4 rejects
        fields[DmaColumns.Longitude] = "0.000000";
        fields[DmaColumns.SpeedOverGround] = string.Empty;  // R5 flags

        var line = new AisSourceLine("f.csv", 2, string.Join(',', fields), fields);
        var judgement = RuleRegistry.Default().Judge(line, RawAisRecordParser.Parse(line));

        Assert.True(judgement.Rejected);
        Assert.Contains(judgement.Hits, h => h.RuleId == "R4");
        Assert.Contains(judgement.Hits, h => h.RuleId == "R5");
    }

    [Fact]
    public void FlagStringHoldsOnlyFlagsNeverRejections()
    {
        var judgement = new RowJudgement([
            new RuleHit("R4", RuleAction.Reject, "sentinel"),
            new RuleHit("R5", RuleAction.Flag, "no sog"),
            new RuleHit("R6", RuleAction.Flag, "fast"),
        ]);

        Assert.Equal("R5,R6", judgement.FlagString);
    }

    [Fact]
    public void FlagStringIsEmptyForACleanRow() =>
        Assert.Equal(string.Empty, new RowJudgement([]).FlagString);
}
