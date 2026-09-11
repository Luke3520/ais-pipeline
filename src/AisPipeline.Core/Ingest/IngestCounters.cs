namespace AisPipeline.Core.Ingest;

/// <summary>
/// What one ingest run did to every row it read. Every row read must land in exactly one of
/// these buckets -- that identity is asserted by <see cref="IsBalanced"/> and is the
/// accounting that makes "nothing is dropped silently" checkable rather than aspirational.
///
/// The two duplicate counters are separate deliberately. 38.2% of a single DMA file is already
/// duplicate on first ingest because the feed merges receiving stations, so one combined
/// counter would read ~38% on a first run and ~100% on a second, and a reader could not tell
/// which part demonstrates idempotency (ADR-0012).
/// </summary>
public sealed record IngestCounters
{
    /// <summary>Every data line read from the source.</summary>
    public long RowsRead { get; init; }

    /// <summary>Rows excluded by the scope guard: not a vessel we are storing.</summary>
    public long RowsFiltered { get; init; }

    /// <summary>Rows newly stored.</summary>
    public long RowsInserted { get; init; }

    /// <summary>Natural key already seen earlier in THIS file. Receiver duplication.</summary>
    public long RowsDuplicateInFile { get; init; }

    /// <summary>Natural key already in the store before this run began. Re-ingest or re-delivery.</summary>
    public long RowsDuplicatePriorRun { get; init; }

    /// <summary>Rows a rule refused.</summary>
    public long RowsQuarantined { get; init; }

    public long Accounted =>
        RowsFiltered + RowsInserted + RowsDuplicateInFile + RowsDuplicatePriorRun + RowsQuarantined;

    /// <summary>
    /// Every row read is accounted for exactly once. A false result means rows went missing
    /// without a record -- blocking class 1.
    /// </summary>
    public bool IsBalanced => Accounted == RowsRead;

    public override string ToString() =>
        $"read {RowsRead:N0}  inserted {RowsInserted:N0}  " +
        $"dup-in-file {RowsDuplicateInFile:N0}  dup-prior-run {RowsDuplicatePriorRun:N0}  " +
        $"quarantined {RowsQuarantined:N0}  filtered {RowsFiltered:N0}";
}
