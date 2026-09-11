using AisPipeline.Core.Sof;

namespace AisPipeline.Tests.Sof;

/// <summary>
/// Every label here is copied verbatim from a real Statement of Facts -- BIMCO's tanker short
/// form, a Peruvian agent's, and a Humber agent's. Three layouts, three vocabularies, the same
/// underlying events.
/// </summary>
public class SofEventClassifierTests
{
    [Theory]
    // BIMCO Standard Statement of Facts (Oil and Chemical Tank Vessels), numbered boxes
    [InlineData("Vessel moored", SofEventKind.AllFast)]
    [InlineData("Vessel arrived at anchorage", SofEventKind.Anchored)]
    [InlineData("Weighed anchor", SofEventKind.AnchorAweigh)]
    [InlineData("Notice of readiness tendered", SofEventKind.NoticeOfReadinessTendered)]
    [InlineData("Loading commenced", SofEventKind.CargoCommenced)]
    [InlineData("Discharging completed", SofEventKind.CargoCompleted)]
    [InlineData("Vessel sailed", SofEventKind.LeftBerth)]
    // Transtotal, Chimbote 2018
    [InlineData("All Fast", SofEventKind.AllFast)]
    [InlineData("Anchored", SofEventKind.Anchored)]
    [InlineData("NOR Tendered", SofEventKind.NoticeOfReadinessTendered)]
    [InlineData("EOSP", SofEventKind.EndOfSeaPassage)]
    [InlineData("Commence Dischargie of GASOLINE 90", SofEventKind.CargoCommenced)]
    // Denholm, Immingham 2023
    [InlineData("Anchor aweigh", SofEventKind.AnchorAweigh)]
    [InlineData("Dropped anchor", SofEventKind.Anchored)]
    [InlineData("First line ashore", SofEventKind.FirstLineAshore)]
    [InlineData("All fast", SofEventKind.AllFast)]
    [InlineData("Commenced discharging", SofEventKind.CargoCommenced)]
    [InlineData("Suspended discharging", SofEventKind.Suspended)]
    [InlineData("Resumed discharging", SofEventKind.Resumed)]
    [InlineData("Completed discharging", SofEventKind.CargoCompleted)]
    [InlineData("Left berth (last line)", SofEventKind.LeftBerth)]
    [InlineData("Start of sea passage (SOSP)", SofEventKind.StartOfSeaPassage)]
    [InlineData("NOR Re-tendered", SofEventKind.NoticeOfReadinessTendered)]
    public void RealLabelsFromRealDocuments(string label, SofEventKind expected) =>
        Assert.Equal(expected, SofEventClassifier.Classify(label));

    [Fact]
    public void AnchorAweighIsNotReadAsDroppingAnchor()
    {
        // Both contain "anchor". Reading one as the other would swap the start and end of an
        // anchorage, and the waiting hours with them.
        Assert.Equal(SofEventKind.AnchorAweigh, SofEventClassifier.Classify("Anchor aweigh"));
        Assert.Equal(SofEventKind.Anchored, SofEventClassifier.Classify("Dropped anchor"));
    }

    [Fact]
    public void LeftBerthIsNotReadAsCargoDespiteTheWordOrder() =>
        Assert.Equal(SofEventKind.LeftBerth, SofEventClassifier.Classify("Left berth (last line)"));

    [Fact]
    public void CompletedAndCommencedAreNotConfused()
    {
        Assert.Equal(SofEventKind.CargoCommenced, SofEventClassifier.Classify("Commenced discharging"));
        Assert.Equal(SofEventKind.CargoCompleted, SofEventClassifier.Classify("Completed discharging"));
    }

    [Theory]
    [InlineData("Start Key Meeting")]
    [InlineData("Finish Ullages")]
    [InlineData("Cargo arm connected")]
    [InlineData("Gangway down")]
    [InlineData("Free pratique granted")]
    public void UnrecognisedLabelsAreKeptRatherThanDropped(string label)
    {
        // A classifier that discarded what it could not name would silently shrink the document,
        // and these are exactly the lines a dispute can turn on.
        Assert.Equal(SofEventKind.Other, SofEventClassifier.Classify(label));
    }

    [Fact]
    public void AnEmptyLabelIsOtherRatherThanAnException() =>
        Assert.Equal(SofEventKind.Other, SofEventClassifier.Classify("   "));
}
