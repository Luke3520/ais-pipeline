namespace AisPipeline.Core.Sof;

/// <summary>
/// Maps a Statement of Facts line label onto what it means.
///
/// The patterns come from real documents, not from imagination: BIMCO's tanker short form
/// (box 5 "Vessel moored", box 20 "Vessel arrived at anchorage", box 28 "Weighed anchor"), a
/// Peruvian agent's form ("All Fast", "Commence Maneuver", "Pilot on Board"), and a Humber
/// agent's ("Anchor aweigh", "First line ashore", "Left berth (last line)", "Suspended
/// discharging"). Three layouts, three vocabularies, the same events.
///
/// Unrecognised labels become <see cref="SofEventKind.Other"/> and are kept. A classifier that
/// dropped what it could not name would silently shrink the document, and the events it failed on
/// are exactly the ones worth reading.
/// </summary>
public static class SofEventClassifier
{
    /// <summary>
    /// Each rule is a set of alternatives, and each alternative is a set of terms that must ALL
    /// appear somewhere in the label.
    ///
    /// Conjunctions rather than phrases, because agents do not write phrases consistently:
    /// "Completed discharging" and "Discharging completed" are the same event with the words
    /// swapped, and "Commence Dischargie" is the same event with a typo. Matching stems in any
    /// order survives all three; matching a phrase survives none of them.
    ///
    /// Ordered, and the first match wins, so completion is tested before commencement.
    /// </summary>
    private static readonly (SofEventKind Kind, string[][] Alternatives)[] Rules =
    [
        (SofEventKind.NoticeOfReadinessTendered, [
            ["notice", "readiness"], ["nor", "tender"], ["nor", "receiv"], ["nor", "accept"]]),

        // Before the looser anchor rules, so "anchor aweigh" is not read as dropping one.
        (SofEventKind.AnchorAweigh, [["anchor", "aweigh"], ["weigh", "anchor"], ["anchor", "up"]]),
        (SofEventKind.Anchored, [["drop", "anchor"], ["anchored"], ["arrived", "anchorage"]]),

        (SofEventKind.AllFast, [["all fast"], ["vessel", "moored"], ["moored", "alongside"]]),
        (SofEventKind.FirstLineAshore, [["first line"], ["first rope"]]),

        // "Left berth (last line)" is the vessel leaving. Deliberately NOT "cast off": both
        // sample documents use that for tugs -- "Tug boat cast off" -- which is a different event
        // by up to half an hour.
        (SofEventKind.LeftBerth, [["left berth"], ["last line"], ["unmoor"], ["vessel", "sailed"]]),

        (SofEventKind.CargoCompleted, [
            ["complet", "discharg"], ["complet", "load"], ["complet", "cargo"], ["finish", "cargo"]]),
        (SofEventKind.CargoCommenced, [
            ["commenc", "discharg"], ["commence", "discharg"], ["commenc", "load"],
            ["commence", "load"], ["commenc", "cargo"], ["start", "cargo"]]),

        (SofEventKind.Resumed, [["resumed"], ["recommenc"], ["restart"]]),
        (SofEventKind.Suspended, [["suspend"], ["stoppage"], ["shore", "stop"]]),

        (SofEventKind.EndOfSeaPassage, [["end of sea passage"], ["eosp"]]),
        (SofEventKind.StartOfSeaPassage, [["start of sea passage"], ["sosp"]]),
    ];

    /// <summary>Classify one label. Case- and punctuation-insensitive.</summary>
    public static SofEventKind Classify(string label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return SofEventKind.Other;
        }

        var normalised = label.Trim().ToLowerInvariant();

        foreach (var (kind, alternatives) in Rules)
        {
            if (alternatives.Any(terms =>
                    terms.All(term => normalised.Contains(term, StringComparison.Ordinal))))
            {
                return kind;
            }
        }

        return SofEventKind.Other;
    }

    /// <summary>
    /// Labels this classifier recognises, for the report that tells a reader what was understood
    /// and what was merely kept.
    /// </summary>
    public static IReadOnlyList<string> KnownPatterns =>
        [.. Rules
            .SelectMany(r => r.Alternatives)
            .Select(terms => string.Join(" + ", terms))
            .OrderBy(p => p, StringComparer.Ordinal)];
}
