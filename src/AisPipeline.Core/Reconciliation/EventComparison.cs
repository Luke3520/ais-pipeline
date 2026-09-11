using AisPipeline.Core.Sof;

namespace AisPipeline.Core.Reconciliation;

/// <summary>What comparing one event against AIS produced.</summary>
public enum ComparisonVerdict
{
    /// <summary>Within tolerance of what AIS observed, once the expected offset is allowed for.</summary>
    Agrees,

    /// <summary>Outside tolerance. Recorded, never resolved -- neither source is declared right.</summary>
    Disagrees,

    /// <summary>
    /// AIS cannot observe this event at all: a notice is an email, hoses are not visible from
    /// space. Reported so the reader sees what is uncorroborated rather than assuming silence
    /// means agreement.
    /// </summary>
    AisCannotObserve,

    /// <summary>The Statement of Facts does not record it.</summary>
    AbsentFromStatement,

    /// <summary>AIS has no corresponding observation -- no anchorage phase, say.</summary>
    AbsentFromAis,
}

/// <summary>
/// One event, as the document tells it and as the transponder tells it.
/// </summary>
/// <param name="Kind">The event.</param>
/// <param name="SofLabel">The words the agent actually wrote, kept for the reader.</param>
/// <param name="SofUtc">When the document says it happened.</param>
/// <param name="AisUtc">When AIS observed the corresponding physical change.</param>
/// <param name="ExpectedOffset">
/// The lag the two should differ by even when both are correct -- 45 minutes for "all fast",
/// nothing for dropping anchor.
/// </param>
/// <param name="Verdict">The outcome.</param>
public sealed record EventComparison(
    SofEventKind Kind,
    string? SofLabel,
    DateTime? SofUtc,
    DateTime? AisUtc,
    TimeSpan ExpectedOffset,
    ComparisonVerdict Verdict)
{
    /// <summary>Raw difference: document minus AIS.</summary>
    public TimeSpan? RawDelta => SofUtc is { } s && AisUtc is { } a ? s - a : null;

    /// <summary>
    /// What is left after allowing for the offset the two sources legitimately differ by. This is
    /// the number that means something; the raw delta is not.
    /// </summary>
    public TimeSpan? UnexplainedDelta =>
        RawDelta is { } raw ? raw - ExpectedOffset : null;

    public override string ToString()
    {
        var sof = SofUtc?.ToString("yyyy-MM-dd HH:mm") ?? "—";
        var ais = AisUtc?.ToString("yyyy-MM-dd HH:mm") ?? "—";
        var delta = UnexplainedDelta is { } d ? $"{d.TotalMinutes,+7:F0} min" : "      —";
        return $"{Kind,-26} SoF {sof}   AIS {ais}   {delta}   {Verdict}";
    }
}
