namespace AisPipeline.Core.Domain;

/// <summary>
/// One line as delivered by an <see cref="Ports.IAisSource"/>: its fields, its raw text, and
/// where it came from.
///
/// The adapter owns how a line is split into fields -- CSV quoting, comment characters,
/// encoding. Core owns what those fields mean. That seam is what keeps the quality rules
/// free of any I/O dependency (ADR-0003).
/// </summary>
/// <param name="SourceFile">File the line came from, for provenance and quarantine.</param>
/// <param name="LineNumber">1-based line number within that file, header included.</param>
/// <param name="RawText">The line exactly as it appeared, kept so a reject carries evidence.</param>
/// <param name="Fields">The split fields, in source order.</param>
public sealed record AisSourceLine(
    string SourceFile,
    long LineNumber,
    string RawText,
    IReadOnlyList<string> Fields);
