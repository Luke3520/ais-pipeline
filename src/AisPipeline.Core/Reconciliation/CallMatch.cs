using System.Text;
using AisPipeline.Core.Domain;
using AisPipeline.Core.Sof;

namespace AisPipeline.Core.Reconciliation;

/// <summary>
/// Whether a Statement of Facts and a port call describe the same visit.
///
/// The question nothing was asking. A document was matched to AIS by MMSI and "most recent
/// complete call", so a statement for a call in March would reconcile against the vessel's most
/// recent call in September, and the difference between two unrelated timelines would be priced in
/// demurrage and printed without a word of doubt. That is worse than a wrong number: it is a wrong
/// number with an audit trail behind it.
///
/// The test is overlap, deliberately, rather than a tuned proximity threshold. A document and the
/// call it describes necessarily share time -- a Notice of Readiness is tendered at the anchorage,
/// which is inside the call, and the last line comes off at its end. Requiring genuine overlap
/// needs no calibrated constant, which matters here: every threshold this project guessed at has
/// since had to move (ADR-0020 moved by a factor of thirty).
/// </summary>
public sealed record CallMatch
{
    public required DateTime DocumentFromUtc { get; init; }
    public required DateTime DocumentToUtc { get; init; }
    public required DateTime AisFromUtc { get; init; }
    public required DateTime AisToUtc { get; init; }

    /// <summary>Shared time. Zero or negative means the two windows do not meet at all.</summary>
    public TimeSpan Overlap { get; init; }

    /// <summary>The port the document names.</summary>
    public required string DocumentPort { get; init; }

    /// <summary>
    /// The port AIS attributed to the call, or null when the gazetteer named none.
    ///
    /// Null for a stop too far from any port to name, and for a store built before the gazetteer
    /// existed. Both mean the same thing to a reader -- AIS is not telling you which port this was.
    /// </summary>
    public string? AisPort { get; init; }

    /// <summary>How far the call sat from that port. Null with <see cref="AisPort"/>.</summary>
    public double? AisPortDistanceNm { get; init; }

    /// <summary>True when AIS had a port to compare at all (ADR-0034 made this reachable).</summary>
    public bool PortWasChecked => AisPort is not null;

    /// <summary>
    /// Whether the two names agree, or null when there is nothing to compare.
    ///
    /// Compared after stripping case, accents and punctuation, so "Arhus" matches "Århus" -- the
    /// World Port Index transliterates, and documents do not. It still cannot match an exonym:
    /// "Goteborg" and "Gothenburg" are the same port and compare as different, and no amount of
    /// normalising fixes that without a synonym table.
    ///
    /// So a false here is a QUESTION, not a finding, and nothing refuses on it. Both names are
    /// reported and a human decides -- the same position rule 4 takes when a vessel's status
    /// contradicts its speed.
    /// </summary>
    public bool? PortNamesAgree => AisPort is null || DocumentPort.Length == 0
        ? null
        : Comparable(DocumentPort) == Comparable(AisPort)
            || Comparable(DocumentPort).Contains(Comparable(AisPort), StringComparison.Ordinal)
            || Comparable(AisPort).Contains(Comparable(DocumentPort), StringComparison.Ordinal);

    public bool TimesOverlap => Overlap > TimeSpan.Zero;

    /// <summary>
    /// The verdict, on the evidence available. Times only, which is why it is named for what it
    /// tested rather than asserting the two are the same call -- a port-name disagreement is
    /// reported but never fails this, because an exonym is indistinguishable from a wrong port.
    /// </summary>
    public bool TimesAreConsistent => TimesOverlap;

    /// <summary>How far apart the windows are when they do not overlap. Zero when they do.</summary>
    public TimeSpan Gap => TimesOverlap ? TimeSpan.Zero : -Overlap;

    public static CallMatch Evaluate(StatementOfFacts sof, PortCall portCall)
    {
        if (sof.Events.Count == 0)
        {
            throw new ArgumentException(
                "the statement has no events, so there is nothing to match against a port call",
                nameof(sof));
        }

        var documentFrom = sof.Events.Min(e => e.TimestampUtc);
        var documentTo = sof.Events.Max(e => e.TimestampUtc);

        // Overlap of two closed intervals. Negative when they are disjoint, and then its magnitude
        // is the gap between them -- which is the number a refusal message needs.
        var overlapStart = documentFrom > portCall.ArrivedUtc ? documentFrom : portCall.ArrivedUtc;
        var overlapEnd = documentTo < portCall.DepartedUtc ? documentTo : portCall.DepartedUtc;

        return new CallMatch
        {
            DocumentFromUtc = documentFrom,
            DocumentToUtc = documentTo,
            AisFromUtc = portCall.ArrivedUtc,
            AisToUtc = portCall.DepartedUtc,
            Overlap = overlapEnd - overlapStart,
            DocumentPort = sof.Port,
            AisPort = portCall.Attribution?.Name,
            AisPortDistanceNm = portCall.Attribution?.DistanceNm,
        };
    }

    /// <summary>One line for a refusal message, naming both windows so the reader can see why.</summary>
    public string Explain() =>
        TimesOverlap
            ? $"document {DocumentFromUtc:yyyy-MM-dd HH:mm}-{DocumentToUtc:yyyy-MM-dd HH:mm} overlaps " +
              $"the AIS call {AisFromUtc:yyyy-MM-dd HH:mm}-{AisToUtc:yyyy-MM-dd HH:mm} " +
              $"by {Overlap.TotalHours:F1}h"
            : $"document covers {DocumentFromUtc:yyyy-MM-dd HH:mm} to {DocumentToUtc:yyyy-MM-dd HH:mm}, " +
              $"the AIS call covers {AisFromUtc:yyyy-MM-dd HH:mm} to {AisToUtc:yyyy-MM-dd HH:mm}; " +
              $"they do not overlap, and are {Gap.TotalHours:F1}h apart";

    /// <summary>
    /// Lowercase, unaccented, letters and digits only.
    ///
    /// The folding is an explicit table rather than <c>string.Normalize(FormD)</c> plus a
    /// combining-mark filter, which is the usual way to do this and does nothing here: this
    /// solution sets <c>InvariantGlobalization</c>, under which Normalize returns the string
    /// unchanged. It fails silently -- "Arhus" and "Århus" simply compared as different ports --
    /// so the table is not a preference, it is the only thing that works. Do not replace it with
    /// Normalize.
    ///
    /// Covers the languages the committed gazetteer actually spans: Danish, Norwegian, Swedish,
    /// German and Polish. A character outside the table keeps its own identity, so an unlisted
    /// accent makes two names compare as different -- which reports a question rather than
    /// inventing an agreement.
    /// </summary>
    private static string Comparable(string name)
    {
        var builder = new StringBuilder(name.Length);

        foreach (var c in name)
        {
            builder.Append(Fold(char.ToLowerInvariant(c)));
        }

        return builder.ToString();
    }

    /// <summary>
    /// One lowercase character, folded to the ASCII letters it stands for. Empty for anything that
    /// is not a letter or digit, which drops the punctuation and spacing documents vary freely.
    /// </summary>
    private static string Fold(char c) => c switch
    {
        // Danish and Norwegian. "aa" rather than "a" for å: Danish writes both Århus and Aarhus
        // for one city, and the World Port Index writes Arhus -- folding to "aa" lets the
        // containment test below reconcile all three.
        'å' => "aa",
        'æ' => "ae",
        'ø' => "o",

        // Swedish and German.
        'ä' => "a",
        'ö' => "o",
        'ü' => "u",
        'ß' => "ss",

        // Polish.
        'ą' => "a",
        'ć' => "c",
        'ę' => "e",
        'ł' => "l",
        'ń' => "n",
        'ó' => "o",
        'ś' => "s",
        'ź' => "z",
        'ż' => "z",

        _ => char.IsLetterOrDigit(c) ? c.ToString() : "",
    };

}
