using AisPipeline.Core.Domain;
using AisPipeline.Core.Ports;

namespace AisPipeline.Core.Detection;

/// <summary>Outcome of one detection pass.</summary>
public sealed record DetectionResult(
    int VesselsExamined,
    int StopsDetected,
    int CompleteStops,
    int PortCalls,
    int StopsWhereStatusDisagrees);

/// <summary>
/// Rebuilds stop events and port calls from stored positions.
///
/// A full recompute, deleting what was there first. `stop_event` and `port_call` are
/// projections over `position_report`, which is the immutable log -- so the way to change a
/// threshold is to re-run this, not to migrate anything. That is the rebuild-from-truth
/// property event sourcing exists to provide, already present here (ADR-0009), and it is what
/// makes running detection twice a genuine no-op.
/// </summary>
public sealed class DetectionPass
{
    private readonly IAisStore _store;
    private readonly StopDetector _detector;
    private readonly PortCallChainer _chainer;

    public DetectionPass(IAisStore store, DetectionThresholds? thresholds = null)
    {
        _store = store;
        var resolved = thresholds ?? DetectionThresholds.Default;
        _detector = new StopDetector(resolved);
        _chainer = new PortCallChainer(resolved);
    }

    public DetectionResult Run()
    {
        var calls = new List<PortCall>();
        var vessels = 0;
        var stops = 0;

        // Fixes arrive ordered by (mmsi, ts_utc), so one vessel's run is contiguous and can be
        // accumulated without holding the whole table in memory.
        foreach (var (_, fixes) in GroupByVessel(_store.ReadFixesOrdered()))
        {
            vessels++;
            var detected = _detector.Detect(fixes);
            if (detected.Count == 0)
            {
                continue;
            }

            stops += detected.Count;
            calls.AddRange(_chainer.Chain(detected));
        }

        _store.ReplaceDetections(calls);

        var allStops = calls.SelectMany(c => c.Phases.Select(p => p.Stop)).ToList();
        return new DetectionResult(
            vessels,
            stops,
            allStops.Count(s => s.IsComplete),
            calls.Count,
            allStops.Count(s => !s.StatusAgrees));
    }

    private static IEnumerable<(long Mmsi, List<PositionFix> Fixes)> GroupByVessel(
        IEnumerable<PositionFix> ordered)
    {
        long? mmsi = null;
        var buffer = new List<PositionFix>();

        foreach (var fix in ordered)
        {
            if (mmsi is { } current && current != fix.Mmsi)
            {
                yield return (current, buffer);
                buffer = [];
            }

            mmsi = fix.Mmsi;
            buffer.Add(fix);
        }

        if (mmsi is { } last && buffer.Count > 0)
        {
            yield return (last, buffer);
        }
    }
}
