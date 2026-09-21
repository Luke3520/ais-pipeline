using AisPipeline.Core.Annotate;
using AisPipeline.Core.Domain;
using AisPipeline.Core.Ingest;
using AisPipeline.Core.Ports;

namespace AisPipeline.Tests.Ingest;

/// <summary>
/// In-memory <see cref="IAisStore"/>. Exists so the pipeline's branching can be exercised in
/// the Core-only unit suite: it depends on nothing but its ports (ADR-0003), so proving its
/// accounting through a real CSV reader and a real database was more machinery than the
/// question needed -- and left the branches dependent on one fixture keeping its exact shape.
/// </summary>
internal sealed class FakeAisStore : IAisStore
{
    private readonly HashSet<NaturalKey> _stored = [];
    private readonly HashSet<(string File, long Line, string Rule)> _quarantined = [];

    public bool SchemaEnsured { get; private set; }

    public DateTime StartedUtc { get; private set; }

    public DateTime FinishedUtc { get; private set; }

    public IngestCounters? Counters { get; private set; }

    public List<Vessel> Vessels { get; } = [];

    public List<(long RunId, QuarantinedRow Row)> Quarantine { get; } = [];

    public int StoredCount => _stored.Count;

    public void EnsureSchema() => SchemaEnsured = true;

    public long BeginRun(string sourceFile, DateTime startedUtc)
    {
        StartedUtc = startedUtc;
        return 1;
    }

    public void CompleteRun(long runId, DateTime finishedUtc, IngestCounters counters)
    {
        FinishedUtc = finishedUtc;
        Counters = counters;
    }

    /// <summary>Mirrors INSERT OR IGNORE: returns how many rows were genuinely new.</summary>
    public int InsertPositions(long runId, IReadOnlyList<AcceptedPosition> batch) =>
        batch.Count(p => _stored.Add(new NaturalKey(
            p.Record.Mmsi, p.Record.TimestampUtc, p.Record.Latitude, p.Record.Longitude)));

    public void InsertQuarantine(long runId, IReadOnlyList<QuarantinedRow> rows)
    {
        foreach (var row in rows)
        {
            if (_quarantined.Add((row.SourceFile, row.SourceLine, row.Hit.RuleId)))
            {
                Quarantine.Add((runId, row));
            }
        }
    }

    public void UpsertVessels(IReadOnlyList<Vessel> vessels) => Vessels.AddRange(vessels);

    /// <summary>Fixes handed back to the annotate and detection passes, in store order.</summary>
    public List<PositionFix> Fixes { get; } = [];

    public List<PortCall> Detections { get; } = [];

    public int ReplaceDetectionsCalls { get; private set; }

    public IEnumerable<PositionFix> ReadFixesOrdered() =>
        Fixes.OrderBy(f => f.Mmsi).ThenBy(f => f.TimestampUtc).ThenBy(f => f.Id);

    public void UpdateQualityFlags(IReadOnlyList<FlagUpdate> updates)
    {
        foreach (var update in updates)
        {
            var index = Fixes.FindIndex(f => f.Id == update.PositionId);
            if (index >= 0)
            {
                Fixes[index] = Fixes[index] with { QualityFlags = update.QualityFlags };
            }
        }
    }

    /// <summary>Every prune this store was asked for, in order.</summary>
    public List<(DateTime CutoffUtc, long PortCallsArchived, string ArchivePath)> Prunes { get; } = [];

    /// <summary>What the next prune reports removing. The deleting itself is a store concern,
    /// and the integration suite covers it on both engines; what Core decides is WHETHER this is
    /// called at all (ADR-0046).</summary>
    public long FixesRemovedPerPrune { get; set; } = 1_000;

    public long PruneBefore(DateTime cutoffUtc, long portCallsArchived, string archivePath)
    {
        Prunes.Add((cutoffUtc, portCallsArchived, archivePath));
        return FixesRemovedPerPrune;
    }

    public void ReplaceDetections(IReadOnlyList<PortCall> portCalls)
    {
        ReplaceDetectionsCalls++;
        Detections.Clear();
        Detections.AddRange(portCalls);
    }

    public void Dispose()
    {
        // Nothing to release.
    }
}
