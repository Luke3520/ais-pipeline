using System.Globalization;
using AisPipeline.Core.Domain;

namespace AisPipeline.Core.Radar;

public enum LogKind
{
    /// <summary>A detected stop began, at the fix that opened it.</summary>
    Stopped,

    /// <summary>R10: a stop began while the transponder kept claiming the vessel was under way.</summary>
    StoppedClaimingUnderWay,

    /// <summary>The first fix after a detected stop's last one.</summary>
    StopEnded,

    /// <summary>R12: moving while the transponder claims moored, anchored or aground.</summary>
    MovingClaimingStationary,
}

/// <summary>
/// One line of the ship's log: what happened, to whom, when, and the line of the source file it
/// rests on.
///
/// The text is built from stored records only. Every figure in it was either read from a fix or
/// computed by detection, and the citation points at the fix the line was emitted on. A log line
/// is a quotation, not a summary.
/// </summary>
public sealed record LogEntry(DateTime AtUtc, long Mmsi, LogKind Kind, string Text, Citation Citation)
{
    /// <summary>
    /// Timestamps print to the second. A log that showed minutes would be rounding, and laytime
    /// arguments are made from exactly this kind of line (CLAUDE.md: never round timestamps).
    /// </summary>
    public string Render(bool withDate) => string.Create(CultureInfo.InvariantCulture,
        $"{AtUtc.ToString(withDate ? "yyyy-MM-dd HH:mm:ss" : "HH:mm:ss", CultureInfo.InvariantCulture)}  {Text} {Citation}");
}
