using AisPipeline.Core.Quality.Rules;

namespace AisPipeline.Core.Voyage;

/// <summary>A run of consecutive days ahead on which the whole feed carried no ETA at all.</summary>
public sealed record EtaEmptyBand(int FromDays, int ToDays)
{
    public int Days => ToDays - FromDays + 1;
}

/// <summary>How far ahead one ETA pointed, and how many fixes said so.</summary>
public sealed record EtaHorizonBin
{
    public int DaysAhead { get; init; }
    public long Fixes { get; init; }
}

/// <summary>
/// The shape of every ETA in the feed, measured against the fix that carried it.
///
/// The AIS ETA field has no year. It carries month, day, hour and minute and nothing else, so a
/// decoder meeting a date that has already gone by has to assume the next one. A forgotten ETA is
/// therefore never left behind in the past -- it is rolled forward and reappears months ahead.
///
/// The interesting part is not the tail, it is the <em>gap</em>. Real voyage plans decay smoothly
/// away from the present; stale ones pile up near a year. If the two populations are genuinely
/// distinct there will be a run of days carrying no fix at all between them, and the run is
/// measured here rather than assumed (ADR-0041 argued the threshold from it).
/// </summary>
public sealed record EtaHorizon
{
    /// <summary>Fixes carrying an ETA at all -- the denominator.</summary>
    public required long FixesWithAnEta { get; init; }

    /// <summary>Fixes whose ETA had already passed. Rare, and they are the reason the gap matters.</summary>
    public required long FixesWithAPastEta { get; init; }

    /// <summary>One bin per day ahead, contiguous from 0, including the empty ones.</summary>
    public required IReadOnlyList<EtaHorizonBin> Bins { get; init; }

    /// <summary>
    /// Every run of consecutive days carrying no fix at all, bounded on both sides by a day that
    /// does.
    ///
    /// A list, not one band, because the data does not have one. On the seven-day window the
    /// region between the two populations is interrupted twice -- 127 fixes land on day 41 and
    /// five on day 77 -- so picking a single longest run and calling it "the gap" would report
    /// 78-130 and quietly contradict ADR-0041, which names 42-76. Both runs are real. Publishing
    /// all of them is the only version that does not require choosing which fact to tell.
    /// </summary>
    public required IReadOnlyList<EtaEmptyBand> EmptyBands { get; init; }

    /// <summary>Where R13 starts calling an ETA stale.</summary>
    public int FlaggedBeyondDays => R13EtaImplausiblyFarAhead.ImplausibleAfterDays;

    /// <summary>
    /// True when R13's threshold falls inside a run carrying no fix at all, so its exact value
    /// cannot change how any fix is classified.
    ///
    /// The claim ADR-0041 rests on, recomputed every export rather than quoted from the record. A
    /// threshold justified by a gap that has since closed is a threshold nobody is justifying, and
    /// the record itself warned that its numbers came from a slice.
    /// </summary>
    public bool ThresholdSitsInAnEmptyBand =>
        EmptyBands.Any(b => FlaggedBeyondDays >= b.FromDays && FlaggedBeyondDays <= b.ToDays);

    /// <summary>
    /// The run of empty days R13's threshold falls in, or null when it falls on a populated day.
    ///
    /// This is the claim ADR-0041 actually makes, and the only well-defined one: not "the gap"
    /// -- the far half of the distribution is sparse and full of gaps -- but the specific run
    /// that makes the threshold's exact value unable to change any classification.
    /// </summary>
    public EtaEmptyBand? BandContainingTheThreshold =>
        EmptyBands.FirstOrDefault(b => FlaggedBeyondDays >= b.FromDays && FlaggedBeyondDays <= b.ToDays);
}

/// <summary>Bins the horizon and finds the gap. Pure.</summary>
public static class EtaHorizonBuilder
{
    /// <summary>
    /// Ignore runs shorter than this when looking for the gap between the two populations.
    ///
    /// A single empty day inside the smooth part of the distribution is sampling noise on a
    /// seven-day window, not a boundary. Seven is a week: long enough that no ordinary voyage
    /// plan would skip it, short enough not to presuppose the 35-day run this data happens to
    /// show.
    /// </summary>
    public const int ShortestRunWorthReporting = 7;

    public static EtaHorizon Build(IReadOnlyList<EtaHorizonBin> counted)
    {
        var past = counted.Where(b => b.DaysAhead < 0).Sum(b => b.Fixes);
        var ahead = counted.Where(b => b.DaysAhead >= 0).ToDictionary(b => b.DaysAhead, b => b.Fixes);

        // Contiguous from day zero, so an empty day is a bin holding zero rather than a missing
        // key. A chart drawn from sparse bins closes the gap by omitting it, which would hide the
        // one feature this exists to show.
        var last = ahead.Count > 0 ? ahead.Keys.Max() : -1;
        var bins = Enumerable.Range(0, last + 1)
            .Select(day => new EtaHorizonBin { DaysAhead = day, Fixes = ahead.GetValueOrDefault(day) })
            .ToList();

        var bands = EmptyRuns(bins);

        return new EtaHorizon
        {
            FixesWithAnEta = counted.Sum(b => b.Fixes),
            FixesWithAPastEta = past,
            Bins = bins,
            EmptyBands = bands,
        };
    }

    /// <summary>
    /// Every run of consecutive days carrying no fix, bounded on both sides by a day that does.
    ///
    /// A run reaching the end of the data is not a gap between populations -- it is just where the
    /// data stops -- so it does not count. Runs shorter than
    /// <see cref="ShortestRunWorthReporting"/> are sampling noise on a window this short.
    /// </summary>
    private static IReadOnlyList<EtaEmptyBand> EmptyRuns(IReadOnlyList<EtaHorizonBin> bins)
    {
        var runs = new List<EtaEmptyBand>();
        int? runFrom = null;

        foreach (var bin in bins)
        {
            if (bin.Fixes == 0)
            {
                runFrom ??= bin.DaysAhead;
                continue;
            }

            if (runFrom is { } start)
            {
                var band = new EtaEmptyBand(start, bin.DaysAhead - 1);

                if (band.Days >= ShortestRunWorthReporting)
                {
                    runs.Add(band);
                }
            }

            runFrom = null;
        }

        return runs;
    }
}
