using AisPipeline.Core.Domain;

namespace AisPipeline.Core.Ports;

/// <summary>
/// A source of AIS lines. The adapter owns the format -- splitting into fields, quoting,
/// comment characters, decompression -- and Core owns what the fields mean (ADR-0003).
///
/// This is the seam a live NMEA stream would attach to, which is what keeps the
/// "batch to monolith, live stream to distributed" escape hatch real rather than hypothetical.
/// </summary>
public interface IAisSource
{
    /// <summary>Identifier of what is being read, stored as provenance on every row.</summary>
    string SourceName { get; }

    /// <summary>
    /// Every data line in source order. Streams: implementations must not materialise the file,
    /// which is ~17M rows and ~3 GB expanded.
    /// </summary>
    IEnumerable<AisSourceLine> ReadLines();
}
