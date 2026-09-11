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
    /// <remarks>
    /// <para><b>Implementations must be replayable.</b> Ingest reads the source twice -- once to
    /// resolve vessel identity, once to store positions (ADR-0007) -- and both enumerations must
    /// yield the same lines in the same order.</para>
    /// <para>If they diverge, pass 2 filters a different row set against the scope computed in
    /// pass 1, and a tanker's genuine fixes are silently counted as out-of-scope: the exact
    /// defect the two-pass design exists to prevent, reintroduced through the back door. A
    /// non-replayable source -- a live feed, a paginated API -- cannot be used with
    /// <c>IngestPipeline</c> as written. The pipeline checks the row counts agree and refuses
    /// the run if they do not, but that is a backstop, not a licence.</para>
    /// </remarks>
    IEnumerable<AisSourceLine> ReadLines();
}
