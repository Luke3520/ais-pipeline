using AisPipeline.Core.Reconciliation;

namespace AisPipeline.Tests.Reconciliation;

/// <summary>
/// Choosing which port call a document describes.
///
/// ADR-0033 gated the comparison and left the selection as "the vessel's most recent complete
/// call", which refused every historical document. These cover the choice itself (ADR-0035).
/// </summary>
public class CallSelectionTests
{
    private static readonly DateTime T0 = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    private static CallCandidate Call(long id, double fromHour, double toHour) =>
        new(id, T0.AddHours(fromHour), T0.AddHours(toHour));

    private static CallSelection Select(double fromHour, double toHour, params CallCandidate[] calls) =>
        CallSelection.Select(T0.AddHours(fromHour), T0.AddHours(toHour), calls);

    [Fact]
    public void The_only_overlapping_call_is_chosen()
    {
        var selection = Select(0, 70, Call(1, 0, 70));

        Assert.Equal(1, selection.Chosen?.Id);
        Assert.False(selection.NothingOverlaps);
        Assert.False(selection.IsAmbiguous);
        Assert.Empty(selection.AlsoOverlapping);
    }

    [Fact]
    public void The_call_sharing_the_most_time_wins_not_the_most_recent()
    {
        // The defect this replaces: recency picked the call, so a document about an earlier visit
        // was compared against a later one.
        var selection = Select(0, 70, Call(1, 0, 68), Call(2, 69, 140));

        Assert.Equal(1, selection.Chosen?.Id);
        Assert.Single(selection.AlsoOverlapping);
        Assert.Equal(2, selection.AlsoOverlapping[0].Call.Id);
    }

    [Fact]
    public void A_historical_document_selects_its_own_call_not_the_latest()
    {
        // Statements of Facts arrive weeks after the event. This is the case the previous
        // implementation refused outright.
        var selection = Select(-1000, -930, Call(1, -1000, -930), Call(2, 0, 70));

        Assert.Equal(1, selection.Chosen?.Id);
    }

    [Fact]
    public void Nothing_is_chosen_when_no_call_shares_any_time()
    {
        var selection = Select(-5000, -4930, Call(1, 0, 70));

        Assert.True(selection.NothingOverlaps);
        Assert.Null(selection.Chosen);
        Assert.Empty(selection.Ranked);
    }

    [Fact]
    public void A_call_that_merely_touches_the_window_does_not_count_as_overlapping()
    {
        // Strictly positive overlap, the same bar CallMatch applies. A selector looser than the
        // gate would pick a candidate the gate then refuses.
        var selection = Select(0, 70, Call(1, -50, 0), Call(2, 70, 140));

        Assert.True(selection.NothingOverlaps);
    }

    [Fact]
    public void An_exact_tie_refuses_rather_than_picking_one()
    {
        // Two calls sharing exactly as much time. Breaking this by id or recency would silently
        // decide which timeline a demurrage figure is measured against.
        var selection = Select(0, 100, Call(1, -10, 20), Call(2, 80, 110));

        Assert.True(selection.IsAmbiguous);
        Assert.Null(selection.Chosen);

        // Both stay visible: a refusal has to say what it could not choose between.
        Assert.Equal(2, selection.Ranked.Count);
        Assert.Equal(2, selection.AlsoOverlapping.Count);
    }

    [Fact]
    public void A_one_minute_difference_is_enough_to_decide()
    {
        // Never rounded. Laytime is counted to the minute, and a tie-break is a choice about which
        // timeline the money comes from.
        //
        // The minute goes on call 2's START, not its end: the window clips the overlap at hour
        // 100, so moving its end changes nothing. Call 1 overlaps 20h, call 2 one minute less.
        var selection = Select(0, 100, Call(1, -10, 20), Call(2, 80 + (1.0 / 60.0), 110));

        Assert.False(selection.IsAmbiguous);
        Assert.Equal(1, selection.Chosen?.Id);
    }

    [Fact]
    public void Candidates_are_ranked_by_shared_time_descending()
    {
        var selection = Select(0, 100, Call(3, 90, 200), Call(1, 0, 60), Call(2, 61, 85));

        Assert.Equal([1, 2, 3], selection.Ranked.Select(r => r.Call.Id).ToList());
    }

    [Fact]
    public void An_empty_candidate_list_is_not_an_error()
    {
        var selection = Select(0, 70);

        Assert.True(selection.NothingOverlaps);
        Assert.Null(selection.Chosen);
    }
}
