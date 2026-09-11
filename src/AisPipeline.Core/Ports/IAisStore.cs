using AisPipeline.Core.Domain;
using AisPipeline.Core.Ingest;
using AisPipeline.Core.Quality;

namespace AisPipeline.Core.Ports;

/// <summary>A position accepted by the rules, ready to store with its provenance and flags.</summary>
/// <param name="Record">The parsed row.</param>
/// <param name="QualityFlags">Comma-joined ids of rules that flagged but did not reject it.</param>
public sealed record AcceptedPosition(RawAisRecord Record, string QualityFlags);

/// <summary>A row a rule refused, kept with the evidence that justifies the refusal.</summary>
public sealed record QuarantinedRow(string SourceFile, long SourceLine, string RawText, RuleHit Hit);

/// <summary>
/// Storage for the pipeline. One port rather than four because every write in a run belongs to
/// the same transaction: a partial batch that stored positions but not their run record would
/// leave rows whose provenance does not resolve (blocking class 3).
///
/// No SQLite-specific behaviour may leak through this interface -- the Postgres adapter at M3
/// depends on it (ADR-0004).
/// </summary>
public interface IAisStore : IDisposable
{
    /// <summary>Create tables and indexes if absent. Safe to call on an existing database.</summary>
    void EnsureSchema();

    /// <summary>Open a run and return its id. Every stored position references it.</summary>
    long BeginRun(string sourceFile, DateTime startedUtc);

    /// <summary>Close a run, recording its counters.</summary>
    void CompleteRun(long runId, DateTime finishedUtc, IngestCounters counters);

    /// <summary>
    /// Insert a batch, ignoring rows whose natural key is already present.
    /// Returns how many were actually inserted; the caller derives the duplicate count from
    /// the difference, which is why this must report the true insert count (ADR-0012).
    /// </summary>
    int InsertPositions(long runId, IReadOnlyList<AcceptedPosition> batch);

    /// <summary>
    /// Record refused rows. Idempotent on (source_file, source_line, rule_id): without that,
    /// re-ingesting a file would leave position_report flat while quarantine doubled, breaking
    /// the idempotency guarantee through a side door (ADR-0006).
    /// </summary>
    void InsertQuarantine(long runId, IReadOnlyList<QuarantinedRow> rows);

    /// <summary>Upsert vessel identities accumulated during pass 1.</summary>
    void UpsertVessels(IReadOnlyList<Vessel> vessels);
}
