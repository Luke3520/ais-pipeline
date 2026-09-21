using AisPipeline.Core.Voyage;

namespace AisPipeline.Tests.Voyage;

/// <summary>
/// The gap between real voyage plans and stale ones, found by measurement.
///
/// The claim the chart makes is that these are two populations and not one long tail. That claim
/// is only worth making if something actually looks for the run of empty days between them, and
/// declines to draw one when it is not there.
/// </summary>
public class EtaHorizonTests
{
    private static EtaHorizonBin Bin(int day, long fixes) =>
        new() { DaysAhead = day, Fixes = fixes };

    [Fact]
    public void The_run_of_empty_days_between_two_populations_is_found()
    {
        var horizon = EtaHorizonBuilder.Build(
            [Bin(0, 1000), Bin(1, 400), Bin(2, 50), Bin(30, 900), Bin(31, 800)]);

        // Days 3..29 carry nothing, bounded by a day that does on each side.
        var band = Assert.Single(horizon.EmptyBands);
        Assert.Equal(3, band.FromDays);
        Assert.Equal(29, band.ToDays);
        Assert.Equal(27, band.Days);
    }

    [Fact]
    public void Bins_are_contiguous_so_an_empty_day_is_a_zero_and_not_a_missing_one()
    {
        // A chart drawn from sparse bins closes the gap by omitting it, which would hide the one
        // feature this exists to show.
        var horizon = EtaHorizonBuilder.Build([Bin(0, 5), Bin(4, 5)]);

        Assert.Equal([0, 1, 2, 3, 4], horizon.Bins.Select(b => b.DaysAhead));
        Assert.Equal([5, 0, 0, 0, 5], horizon.Bins.Select(b => b.Fixes));
    }

    [Fact]
    public void A_single_quiet_day_inside_the_smooth_part_is_not_a_band()
    {
        // Sampling noise on a seven-day window, not a boundary between populations.
        var horizon = EtaHorizonBuilder.Build(
            [Bin(0, 900), Bin(1, 500), Bin(3, 300), Bin(4, 100)]);

        Assert.Empty(horizon.EmptyBands);
    }

    [Fact]
    public void A_run_that_reaches_the_end_of_the_data_is_where_the_data_stops_not_a_gap()
    {
        // Nothing bounds it on the right, so it says only that no ETA pointed further -- which is
        // a fact about the window, not about two populations.
        var horizon = EtaHorizonBuilder.Build([Bin(0, 900), Bin(1, 500)]);

        Assert.Empty(horizon.EmptyBands);
    }

    [Fact]
    public void Every_run_is_reported_and_not_only_the_longest()
    {
        // This is the shape the real feed has: a sparse region interrupted by a handful of
        // stragglers. Reporting only the longest run would name 78-130 on that data and quietly
        // contradict ADR-0041, which names 42-76. Both are true.
        var horizon = EtaHorizonBuilder.Build(
            [Bin(0, 900), Bin(41, 127), Bin(77, 5), Bin(131, 6000)]);

        Assert.Equal(
            [(1, 40), (42, 76), (78, 130)],
            horizon.EmptyBands.Select(b => (b.FromDays, b.ToDays)));
    }

    [Fact]
    public void The_band_the_threshold_falls_in_is_named()
    {
        // The claim ADR-0041 makes is about one specific run, not about "the gap": the far half
        // of the distribution is sparse and full of runs, and only this one bears on whether the
        // threshold's value can change a classification.
        var horizon = EtaHorizonBuilder.Build(
            [Bin(0, 900), Bin(41, 127), Bin(77, 5), Bin(131, 6000)]);

        var band = horizon.BandContainingTheThreshold;

        Assert.NotNull(band);
        Assert.Equal(42, band.FromDays);
        Assert.Equal(76, band.ToDays);
        Assert.Equal(35, band.Days);
    }

    [Fact]
    public void An_eta_already_in_the_past_is_counted_and_kept_out_of_the_bins()
    {
        // The rollover is supposed to make this population impossible, so its size is the check on
        // that story rather than a rounding detail.
        var horizon = EtaHorizonBuilder.Build([Bin(-3, 40), Bin(-1, 60), Bin(0, 900)]);

        Assert.Equal(100, horizon.FixesWithAPastEta);
        Assert.Equal(1000, horizon.FixesWithAnEta);
        Assert.Equal(0, horizon.Bins[0].DaysAhead);
    }

    [Fact]
    public void The_r13_threshold_is_reported_as_sitting_inside_the_band_or_not()
    {
        // ADR-0041 argued the threshold from the gap. A threshold justified by a gap that has
        // since closed is a threshold nobody is justifying, so the claim is recomputed rather
        // than quoted.
        // 60 falls inside 42-76 even though 78-130 is the longer run -- which is the whole reason
        // every band is published rather than the biggest one.
        var real = EtaHorizonBuilder.Build(
            [Bin(0, 900), Bin(41, 127), Bin(77, 5), Bin(131, 6000)]);
        Assert.True(real.ThresholdSitsInAnEmptyBand);

        // A fix landing on day 60 itself would mean the threshold decides something.
        var populated = EtaHorizonBuilder.Build(
            [Bin(0, 900), Bin(41, 127), Bin(60, 3), Bin(131, 6000)]);
        Assert.False(populated.ThresholdSitsInAnEmptyBand);

        var noBand = EtaHorizonBuilder.Build([Bin(0, 10), Bin(1, 10)]);
        Assert.False(noBand.ThresholdSitsInAnEmptyBand);
    }

    [Fact]
    public void An_empty_store_produces_an_empty_horizon_rather_than_throwing()
    {
        var horizon = EtaHorizonBuilder.Build([]);

        Assert.Empty(horizon.Bins);
        Assert.Equal(0, horizon.FixesWithAnEta);
        Assert.Empty(horizon.EmptyBands);
        Assert.Null(horizon.BandContainingTheThreshold);
    }
}
