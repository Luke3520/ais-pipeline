using System.Globalization;
using System.Text.RegularExpressions;
using AisPipeline.Core.Domain;
using AisPipeline.Core.Radar;

namespace AisPipeline.Core.Narration;

/// <summary>What the validator found: the sentences it read, and every reason to refuse them.</summary>
public sealed record NarrationVerdict(IReadOnlyList<string> Sentences, IReadOnlyList<string> Problems)
{
    public bool Accepted => Sentences.Count > 0 && Problems.Count == 0;
}

/// <summary>
/// Decides whether a narrated ship's log may be printed. Pure, deterministic, and deliberately
/// unforgiving.
///
/// A language model writing about the data is a new way for a number nobody can trace to reach the
/// page. So the model's prose is held to the rule every other output already obeys (CLAUDE.md rule
/// 1), mechanically:
///
/// 1. **Every sentence cites.** It carries at least one <c>[rN·LM]</c>.
/// 2. **Every citation is real.** It names an entry the model was actually given. A citation to a
///    line that exists in the database but was not in the evidence is still refused, because the
///    model cannot have read it.
/// 3. **Every number is quoted.** Each figure in a sentence (a time, a duration, a speed, a date
///    part) appears in the entries that sentence cites. A model that adds two durations, rounds
///    14.06 to 14, or invents an hour has written a number that is in none of them, and the
///    sentence is refused.
///
/// What this cannot catch is a false claim made of words alone: "she was waiting for orders" cites
/// fine and contains no number. The prompt forbids motive and the README says plainly what is and
/// is not checked. A validator that claimed to catch everything would be the bigger lie.
///
/// Nothing is dropped silently. A refused narration is not trimmed down to its good sentences: the
/// caller prints the problems and the deterministic log instead.
/// </summary>
public static partial class CitedNarrative
{
    public static NarrationVerdict Validate(string text, IReadOnlyList<LogEntry> evidence)
    {
        var byCitation = evidence
            .GroupBy(e => e.Citation)
            .ToDictionary(g => g.Key, g => g.ToList());

        var sentences = Sentences(text);
        var problems = new List<string>();

        if (sentences.Count == 0)
        {
            problems.Add("the narration is empty");
        }

        for (var i = 0; i < sentences.Count; i++)
        {
            var sentence = sentences[i];
            var label = string.Create(CultureInfo.InvariantCulture, $"sentence {i + 1}");
            var cited = Citation.FindAll(sentence);

            if (cited.Count == 0)
            {
                problems.Add($"{label} cites nothing: \"{sentence}\"");
                continue;
            }

            var unknown = cited.Where(c => !byCitation.ContainsKey(c)).ToList();
            foreach (var c in unknown)
            {
                problems.Add($"{label} cites {c}, which is not in the log it was given");
            }

            if (unknown.Count > 0)
            {
                continue;
            }

            var entries = cited.SelectMany(c => byCitation[c]).ToList();

            // Typed facts, compared kind to kind. Taking every figure from the whole rendered entry
            // let an entry dated 2026-09-04 license a bare "9" or "4", so "waited 4 hours" passed
            // as quoted. Small integers are exactly what a model invents, so the date's parts are
            // not figures: a date matches only a date, a time only a time, and everything else
            // only the figures in the entry's text (durations, speeds, names).
            var dates = entries.Select(e => (e.AtUtc.Month, e.AtUtc.Day)).ToHashSet();
            var facts = new HashSet<string>(
                entries.SelectMany(e => Figures(WithoutCitations(e.Text))
                    .Concat(Figures(e.AtUtc.ToString("HH:mm:ss", CultureInfo.InvariantCulture)))),
                StringComparer.Ordinal);

            var (remainder, stated) = TakeDates(WithoutCitations(sentence));
            foreach (var (month, day, written) in stated)
            {
                if (!dates.Contains((month, day)))
                {
                    problems.Add($"{label} says {written}, which none of its citations is dated");
                }
            }

            foreach (var figure in Figures(remainder).Distinct())
            {
                if (!facts.Contains(figure))
                {
                    problems.Add($"{label} says {figure}, which none of its citations contain");
                }
            }
        }

        return new NarrationVerdict(sentences, problems);
    }

    /// <summary>
    /// Sentences, split after a full stop, question or exclamation mark followed by space. A
    /// citation the model put after the full stop, "...anchor. [r3·L9]", belongs to the sentence
    /// before it, not the one after. A decimal point is never followed by a space, so "30.4 h"
    /// stays whole.
    /// </summary>
    public static IReadOnlyList<string> Sentences(string text)
    {
        var sentences = new List<string>();

        foreach (var piece in SentenceBreak().Split(text.Replace('\n', ' ')))
        {
            var rest = piece.Trim();
            var lead = LeadingCitations().Match(rest);
            if (lead.Success && sentences.Count > 0)
            {
                sentences[^1] = sentences[^1] + " " + lead.Value.Trim();
                rest = rest[lead.Length..].Trim();
            }

            if (rest.Length > 0)
            {
                sentences.Add(rest);
            }
        }

        return sentences;
    }

    /// <summary>
    /// Every figure in a piece of text, normalised so the same quantity compares equal however it
    /// was written. A whole number loses leading zeros, so "09" and "9" are one figure. A time of day also yields its hours and minutes, so a sentence
    /// may say 22:02 for an entry stamped 22:02:21. That is truncation, which drops the seconds
    /// and does not move the minute. Rounding would move it, and rounding is refused.
    /// </summary>
    public static IEnumerable<string> Figures(string text)
    {
        foreach (Match m in Figure().Matches(text))
        {
            var raw = m.Value;
            if (raw.Contains(':', StringComparison.Ordinal))
            {
                yield return raw;
                var parts = raw.Split(':');
                if (parts.Length == 3)
                {
                    yield return $"{parts[0]}:{parts[1]}";
                }
            }
            else if (raw.Contains('.', StringComparison.Ordinal))
            {
                yield return raw;
            }
            else
            {
                var trimmed = raw.TrimStart('0');
                yield return trimmed.Length == 0 ? "0" : trimmed;
            }
        }
    }

    /// <summary>
    /// Dates written as dates, lifted out of a sentence before its figures are read: ISO
    /// (2026-09-04), "4 September" or "September 4", with an optional ordinal suffix and year. Each
    /// is compared as a whole calendar day against the dates of the cited entries. A day number on
    /// its own, with no month beside it, is not a date and is read as an ordinary figure.
    /// </summary>
    public static (string Remainder, IReadOnlyList<(int Month, int Day, string Written)> Dates) TakeDates(string sentence)
    {
        var found = new List<(int, int, string)>();
        var rest = IsoDate().Replace(sentence, m =>
        {
            found.Add((int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture),
                int.Parse(m.Groups["d"].Value, CultureInfo.InvariantCulture), m.Value));
            return " ";
        });
        rest = WrittenDate().Replace(rest, m =>
        {
            var month = Array.IndexOf(Months, m.Groups["month1"].Success
                ? m.Groups["month1"].Value.ToLowerInvariant()
                : m.Groups["month2"].Value.ToLowerInvariant()) + 1;
            var day = int.Parse(m.Groups["day1"].Success ? m.Groups["day1"].Value : m.Groups["day2"].Value,
                CultureInfo.InvariantCulture);
            found.Add((month, day, m.Value.Trim()));
            return " ";
        });
        return (rest, found);
    }

    private static readonly string[] Months =
    [
        "january", "february", "march", "april", "may", "june",
        "july", "august", "september", "october", "november", "december",
    ];

    /// <summary>
    /// Citations are provenance, not facts. Left in, "[r4·L16113623]" would make 4 a figure every
    /// sentence citing run 4 was allowed to state.
    /// </summary>
    private static string WithoutCitations(string text) => Citation.Pattern().Replace(text, " ");

    [GeneratedRegex(@"\b\d{4}-(?<m>\d{2})-(?<d>\d{2})\b")]
    private static partial Regex IsoDate();

    [GeneratedRegex(@"\b(?:(?<day1>\d{1,2})(?:st|nd|rd|th)?\s+(?:of\s+)?(?<month1>January|February|March|April|May|June|July|August|September|October|November|December)|(?<month2>January|February|March|April|May|June|July|August|September|October|November|December)\s+(?<day2>\d{1,2})(?:st|nd|rd|th)?)\b(?:,?\s+\d{4})?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WrittenDate();

    [GeneratedRegex(@"(?<=[.!?])\s+")]
    private static partial Regex SentenceBreak();

    [GeneratedRegex(@"^(\[r\d+[·.]L\d+\]\s*)+")]
    private static partial Regex LeadingCitations();

    [GeneratedRegex(@"\d+(?::\d+)+|\d+\.\d+|\d+")]
    private static partial Regex Figure();
}
