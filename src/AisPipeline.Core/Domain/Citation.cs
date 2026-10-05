using System.Globalization;
using System.Text.RegularExpressions;

namespace AisPipeline.Core.Domain;

/// <summary>
/// Where a statement came from: one ingest run and one line of the file that run read.
///
/// Every stored fix already carries this pair (CLAUDE.md rule 1). This type exists because text
/// now carries it too. The ship's log and the narration print sentences about the data, and a
/// sentence nobody can trace back to a broadcast is the same defect as a number nobody can trace.
///
/// Printed as <c>[r3·L48211]</c>: short enough to sit at the end of a log line, and exact enough
/// to answer with one query,
/// <c>SELECT * FROM position_report WHERE ingest_run_id = 3 AND source_line = 48211</c>.
/// </summary>
public readonly partial record struct Citation(long RunId, long SourceLine)
{
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"[r{RunId}·L{SourceLine}]");

    /// <summary>
    /// Every citation in a piece of text, in order.
    ///
    /// Accepts a plain dot as well as the middle dot, because a language model asked to
    /// reproduce <c>·</c> sometimes types <c>.</c> instead, and rejecting a sentence for its
    /// typography would punish the wrong thing. Anything else that looks almost like a citation is
    /// not one.
    /// </summary>
    public static IReadOnlyList<Citation> FindAll(string text) =>
        [.. Pattern().Matches(text).Select(m => new Citation(
            long.Parse(m.Groups["run"].Value, CultureInfo.InvariantCulture),
            long.Parse(m.Groups["line"].Value, CultureInfo.InvariantCulture)))];

    [GeneratedRegex(@"\[r(?<run>\d{1,18})[·.]L(?<line>\d{1,18})\]", RegexOptions.CultureInvariant)]
    internal static partial Regex Pattern();
}
