namespace AisPipeline.Core.Voyage;

/// <summary>What a destination string turned out to be, decided by its shape alone.</summary>
public enum DestinationShape
{
    /// <summary>Five characters: a two-letter country and a three-character place. The standard.</summary>
    UnLocode,

    /// <summary>Two or more places with an arrow between them. A route, in a field sized for one place.</summary>
    Route,

    /// <summary>The vessel does not know yet. A real commercial state, not an omission.</summary>
    ForOrders,

    /// <summary>A place, written the way a person would say it.</summary>
    FreeText,

    /// <summary>Characters no keyboard puts there on purpose.</summary>
    Unreadable,
}

/// <summary>One distinct string, as typed, with how much of the feed carried it.</summary>
public sealed record DestinationCount
{
    public string Text { get; init; } = "";
    public long Vessels { get; init; }
    public long Fixes { get; init; }
}

/// <summary>A typed destination with the shape it was classified as.</summary>
public sealed record TypedDestination
{
    public required string Text { get; init; }
    public required long Vessels { get; init; }
    public required long Fixes { get; init; }
    public required DestinationShape Shape { get; init; }

    /// <summary>Upper-cased with spacing removed: the form two spellings must share to be the same.</summary>
    public required string Normalised { get; init; }
}

/// <summary>
/// What the crew typed where the destination goes.
///
/// The field is free text, and this is the most human thing in the feed. It holds UN/LOCODEs, port
/// names in three languages, nautical nicknames, whole routes with an arrow in the middle, pilot
/// exemption numbers, and occasionally characters no keyboard produces on purpose.
///
/// Every judgement here is about the <em>shape</em> of a string and never about which place it
/// means. Deciding that SKAW and SKAGEN and DKSKA are one port needs a gazetteer and a decision
/// about spelling; deciding that DKSKA is LOCODE-shaped and SKAGEN is not needs only the string.
/// The first would be a claim this data cannot support, so it is not made.
/// </summary>
public static class DestinationClassifier
{
    /// <summary>Characters a person could reasonably type into this field.</summary>
    private const string Punctuation = "><=-/.#,()&+'_:\" ";

    /// <summary>
    /// Characters crews use to mean "and then".
    ///
    /// Both directions, because both appear: DKSKA&gt;PLGDN and SEGOT&lt;SEKLR are the same idea
    /// written by people who disagree about which end the arrow points. "=&gt;" turns up too, which
    /// is why "=" is readable punctuation rather than debris -- an early version of this called
    /// PLGDN=&gt;NOMON unreadable, which said more about the classifier than about the crew.
    /// </summary>
    private const string RouteArrows = "><";

    public static TypedDestination Classify(DestinationCount counted)
    {
        var text = counted.Text;

        return new TypedDestination
        {
            Text = text,
            Vessels = counted.Vessels,
            Fixes = counted.Fixes,
            Normalised = Normalise(text),
            Shape = ShapeOf(text),
        };
    }

    /// <summary>
    /// Upper-cased with every space removed.
    ///
    /// The one collapsing rule applied, and it is deliberately the weakest one available: "SE GOT"
    /// and "SEGOT" are the same five characters typed with and without a thumb on the space bar.
    /// Anything stronger -- stemming, edit distance, a port gazetteer -- starts deciding that two
    /// different words mean the same place, which is a claim about the world rather than about
    /// the string.
    /// </summary>
    public static string Normalise(string text) =>
        new([.. text.ToUpperInvariant().Where(c => c != ' ')]);

    private static DestinationShape ShapeOf(string text)
    {
        if (text.Any(c => !char.IsAsciiLetterOrDigit(c) && !Punctuation.Contains(c)))
        {
            return DestinationShape.Unreadable;
        }

        var upper = text.ToUpperInvariant();

        // Checked before the arrow, because "SKAW FOR ORDER" and "EG SUZ FOR ORDER" are the same
        // statement -- the vessel has no destination yet -- however much else is in the string.
        if (upper.Contains("ORDER", StringComparison.Ordinal))
        {
            return DestinationShape.ForOrders;
        }

        if (text.Any(RouteArrows.Contains))
        {
            return DestinationShape.Route;
        }

        return IsLocodeShaped(Normalise(text)) ? DestinationShape.UnLocode : DestinationShape.FreeText;
    }

    /// <summary>
    /// Five characters, the first two letters: the shape of a UN/LOCODE.
    ///
    /// Shape only. Nothing here checks the code exists -- that needs the UN/LOCODE list, and the
    /// interesting claim is about how many crews reached for the standard at all, not about which
    /// of them got it right.
    /// </summary>
    private static bool IsLocodeShaped(string normalised) =>
        normalised.Length == 5
        && char.IsAsciiLetter(normalised[0])
        && char.IsAsciiLetter(normalised[1])
        && normalised[2..].All(char.IsAsciiLetterOrDigit);
}

/// <summary>Everything the feed was told about where its ships were going.</summary>
public sealed record TypedDestinations
{
    public required long FixesWithADestination { get; init; }
    public required long VesselsTyping { get; init; }

    /// <summary>Vessels that typed more than one distinct string across the window.</summary>
    public required long VesselsThatChangedIt { get; init; }

    public required IReadOnlyList<TypedDestination> Destinations { get; init; }

    /// <summary>Distinct strings, exactly as typed.</summary>
    public int DistinctStrings => Destinations.Count;

    /// <summary>Distinct strings once spacing and case stop counting.</summary>
    public int DistinctIgnoringSpacingAndCase =>
        Destinations.Select(d => d.Normalised).Distinct(StringComparer.Ordinal).Count();

    public IReadOnlyList<TypedDestination> OfShape(DestinationShape shape) =>
        [.. Destinations.Where(d => d.Shape == shape).OrderByDescending(d => d.Fixes)];

    public long FixesOfShape(DestinationShape shape) =>
        Destinations.Where(d => d.Shape == shape).Sum(d => d.Fixes);
}

/// <summary>Classifies the lot. Pure.</summary>
public static class TypedDestinationsBuilder
{
    public static TypedDestinations Build(
        IReadOnlyList<DestinationCount> counted, long vesselsTyping, long vesselsThatChangedIt) =>
        new()
        {
            FixesWithADestination = counted.Sum(d => d.Fixes),
            VesselsTyping = vesselsTyping,
            VesselsThatChangedIt = vesselsThatChangedIt,
            Destinations = [.. counted
                .Select(DestinationClassifier.Classify)
                .OrderByDescending(d => d.Fixes)],
        };
}

/// <summary>How many vessels typed anything, and how many ever changed what they typed.</summary>
public sealed record DestinationTypists
{
    public long Vessels { get; init; }
    public long VesselsThatChangedIt { get; init; }
}
