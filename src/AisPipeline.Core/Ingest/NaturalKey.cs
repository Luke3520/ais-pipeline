namespace AisPipeline.Core.Ingest;

/// <summary>
/// The natural key that makes ingest idempotent: (mmsi, timestamp, latitude, longitude).
///
/// Position is part of the key deliberately. In a 1.7M-row sample, 658,580 rows share
/// (mmsi, timestamp) while only 654,880 share the full key -- so ~3,700 rows are the same
/// vessel-second reported at different positions by different receivers. Those are real
/// disagreements and collapsing them here would destroy the evidence (ADR-0005).
/// </summary>
public readonly record struct NaturalKey(long Mmsi, DateTime TimestampUtc, double Latitude, double Longitude);
