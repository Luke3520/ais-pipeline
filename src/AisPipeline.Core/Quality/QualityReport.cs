using System.Globalization;
using AisPipeline.Core.Query;

namespace AisPipeline.Core.Quality;

/// <summary>One line of the quality report: a rule, and what it did to the data.</summary>
public sealed record QualityReportLine(
    string RuleId,
    string Description,
    long Quarantined,
    long Flagged)
{
    public long Total => Quarantined + Flagged;

    /// <summary>
    /// True when the rule is registered but has never fired on the data in the store.
    ///
    /// Distinct from absent. R6 has never fired on the seven-day window and neither has the
    /// teleport gate on some slices; that is a fact about the feed worth printing, not a row to
    /// omit. An omitted rule reads as a rule that does not exist.
    /// </summary>
    public bool Silent => Total == 0;
}

/// <summary>
/// Builds the quality report from what the store counted and what the pipeline knows it runs.
///
/// Pure, and in Core, because the interesting part is not the aggregation -- the adapter does
/// that -- but the reconciliation of two lists that disagree. The store only knows the rules
/// that left a mark; the registry knows every rule that ran. A rule in the registry and absent
/// from the counts fired zero times and must say so. A rule in the counts and absent from the
/// registry is the alarming case: rows in the store carry an id nothing currently runs, which
/// means a retired or renamed rule, and hiding it would quietly orphan those rows (rule 2).
/// </summary>
public static class QualityReport
{
    public static IReadOnlyList<QualityReportLine> Build(
        RuleRegistry registry,
        IReadOnlyList<RuleHitCount> counts)
    {
        var byId = counts.ToDictionary(c => c.RuleId, StringComparer.Ordinal);
        var lines = new List<QualityReportLine>();

        foreach (var rule in registry.All)
        {
            var count = byId.GetValueOrDefault(rule.Id);
            lines.Add(new QualityReportLine(
                rule.Id,
                rule.Description,
                count?.Quarantined ?? 0,
                count?.Flagged ?? 0));
        }

        var known = registry.All.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var orphan in counts.Where(c => !known.Contains(c.RuleId)))
        {
            lines.Add(new QualityReportLine(
                orphan.RuleId,
                "no rule with this id is registered -- retired, or renamed without a migration",
                orphan.Quarantined,
                orphan.Flagged));
        }

        return [.. lines.OrderBy(l => Ordinal(l.RuleId)).ThenBy(l => l.RuleId, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Sort R1, R4, R11 -- not R1, R11, R4.
    ///
    /// Ids are a letter and a number, so ordering them as text puts R11 between R1 and R4. The
    /// report is read by a human looking for a specific rule, and the ids are quoted in numeric
    /// order everywhere else in the project.
    /// </summary>
    private static int Ordinal(string ruleId) =>
        ruleId.Length > 1
        && int.TryParse(ruleId.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            ? n
            : int.MaxValue;
}
