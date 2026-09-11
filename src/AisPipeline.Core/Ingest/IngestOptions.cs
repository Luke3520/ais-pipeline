namespace AisPipeline.Core.Ingest;

/// <summary>Knobs for one ingest run.</summary>
public sealed record IngestOptions
{
    /// <summary>
    /// Keep only vessels whose resolved ship type matches, e.g. "Tanker". Null keeps everything.
    ///
    /// Matched against the vessel's identity resolved in pass 1, never against an individual
    /// row's ship-type field: static data rides only on message-type-5 rows, so filtering per
    /// row drops 2.6% of a tanker's own fixes (ADR-0007).
    /// </summary>
    public string? ShipType { get; init; }

    /// <summary>Rows inserted per transaction.</summary>
    public int BatchSize { get; init; } = 5_000;

    /// <summary>Stop after this many source lines. For the development loop, not production runs.</summary>
    public long? Limit { get; init; }
}
