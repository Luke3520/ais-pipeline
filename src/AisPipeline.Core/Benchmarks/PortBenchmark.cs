using AisPipeline.Core.Geo;
using AisPipeline.Core.Query;

namespace AisPipeline.Core.Benchmarks;

/// <summary>
/// How long calls at one port actually took, and how many were thrown away to say so.
///
/// The question this answers is "is 40 hours at Skagen normal?", and the honest answer needs three
/// things: a middle, a spread, and the size of the sample it rests on. A median printed without its
/// n is a number the reader cannot weigh (ADR-0043).
/// </summary>
public sealed record PortBenchmark
{
    public required int WpiNumber { get; init; }
    public required string PortName { get; init; }
    public required string Country { get; init; }

    /// <summary>Calls attributed to this port, before any were excluded.</summary>
    public required int AttributedCalls { get; init; }

    /// <summary>Calls whose true extent is unknown, so their hours are lower bounds.</summary>
    public required int ExcludedIncomplete { get; init; }

    /// <summary>Calls nearest this port but too far off to be counted as its own.</summary>
    public required int ExcludedTooFar { get; init; }

    /// <summary>What the figures below are computed from.</summary>
    public required int UsableCalls { get; init; }

    /// <summary>Waiting hours, ascending. Published so a reader can check the percentiles.</summary>
    public required IReadOnlyList<double> WaitingHours { get; init; }

    public required IReadOnlyList<double> WorkingHours { get; init; }

    public double? MedianWaitingHours => Percentile(WaitingHours, 50, BenchmarkMinimums.ForMedian);

    public double? MedianWorkingHours => Percentile(WorkingHours, 50, BenchmarkMinimums.ForMedian);

    /// <summary>The long tail: congestion rather than the usual call.</summary>
    public double? P90WaitingHours => Percentile(WaitingHours, 90, BenchmarkMinimums.ForPercentile);

    /// <summary>
    /// Where a given wait sits against this port's record, as a percentage of calls at or below it.
    ///
    /// Null when the sample is too small to rank against. This is the actual question an agent
    /// asks -- not "what is the median" but "was mine unusual" -- and the answer is a position in
    /// a distribution, not a verdict.
    /// </summary>
    public double? RankWaiting(double hours) =>
        UsableCalls < BenchmarkMinimums.ForMedian
            ? null
            : 100.0 * WaitingHours.Count(h => h <= hours) / WaitingHours.Count;

    /// <summary>
    /// Nearest-rank percentile: the smallest observed value at or above the requested position.
    ///
    /// Not interpolated. With samples this small, interpolating invents a duration that no vessel
    /// experienced, and the whole point of the figure is that it describes calls that happened.
    /// </summary>
    private static double? Percentile(IReadOnlyList<double> ascending, int percentile, int minimum)
    {
        if (ascending.Count < minimum)
        {
            return null;
        }

        var rank = (int)Math.Ceiling(percentile / 100.0 * ascending.Count);
        return ascending[Math.Clamp(rank - 1, 0, ascending.Count - 1)];
    }
}

/// <summary>How much evidence a figure needs before it is published at all.</summary>
public static class BenchmarkMinimums
{
    /// <summary>
    /// Calls needed before a median is reported. Five.
    ///
    /// Below this the middle of the sample is an anecdote: with four calls the median is the mean
    /// of the middle two, and one congested call moves it by hours. Stated rather than tuned, and
    /// the count is always published so a reader can disagree.
    /// </summary>
    public const int ForMedian = 5;

    /// <summary>
    /// Calls needed before a 90th percentile is reported. Ten.
    ///
    /// A p90 from nine observations is the largest of nine, which is not a tail estimate, it is a
    /// maximum wearing a percentile's name.
    /// </summary>
    public const int ForPercentile = 10;
}

/// <summary>
/// Builds the benchmarks, and counts what it refused to use.
///
/// Pure. The exclusions are the interesting part: on the seven-day window 357 attributed calls
/// become 119 usable ones, and a reader who is not told that is being handed a median drawn from a
/// third of the data without knowing it.
/// </summary>
public static class PortBenchmarkBuilder
{
    public static IReadOnlyList<PortBenchmark> Build(IReadOnlyList<PortCallHours> calls) =>
        [.. calls
            .GroupBy(c => (c.WpiNumber, c.PortName, c.Country))
            .Select(group =>
            {
                // Incomplete first, so a call that is both incomplete and too far is counted once,
                // under the reason that would disqualify it on its own.
                var incomplete = group.Where(c => !c.IsComplete).ToList();
                var complete = group.Where(c => c.IsComplete).ToList();

                // A call attributed at 14 nm is nearest to this port, not at it; averaging it in
                // would put an anchorage in open water into a berth benchmark (ADR-0034).
                var tooFar = complete
                    .Where(c => c.DistanceNm > PortAttributionThresholds.PlausiblyAtPortNm)
                    .ToList();

                var usable = complete
                    .Where(c => c.DistanceNm <= PortAttributionThresholds.PlausiblyAtPortNm)
                    .ToList();

                return new PortBenchmark
                {
                    WpiNumber = group.Key.WpiNumber,
                    PortName = group.Key.PortName,
                    Country = group.Key.Country,
                    AttributedCalls = group.Count(),
                    ExcludedIncomplete = incomplete.Count,
                    ExcludedTooFar = tooFar.Count,
                    UsableCalls = usable.Count,
                    WaitingHours = [.. usable.Select(c => c.WaitingHours).Order()],
                    WorkingHours = [.. usable.Select(c => c.WorkingHours).Order()],
                };
            })
            .OrderByDescending(b => b.UsableCalls)
            .ThenBy(b => b.PortName, StringComparer.Ordinal)];
}
