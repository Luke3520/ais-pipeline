namespace AisPipeline.Core.Sof;

/// <summary>
/// What a Statement of Facts line means, independent of how a particular agent worded it.
///
/// Real forms do not agree on labels. A BIMCO tanker form has box 5 "Vessel moored"; a Peruvian
/// agent writes "All Fast"; a Humber agent writes "All fast" among thirty-six free-text rows. They
/// are the same physical event, and the comparison anchors on the event, never on the wording.
/// </summary>
public enum SofEventKind
{
    /// <summary>Not one of the events this pipeline reasons about. Kept, never discarded.</summary>
    Other,

    /// <summary>End of sea passage: the vessel arrives off the port.</summary>
    EndOfSeaPassage,

    /// <summary>Anchor down. AIS sees a vessel stop, so this is corroborable.</summary>
    Anchored,

    /// <summary>Anchor up. AIS sees a vessel start moving, so this is corroborable.</summary>
    AnchorAweigh,

    /// <summary>
    /// Notice of Readiness tendered. An email -- AIS cannot observe it, and any comparison
    /// involving it is one-sided by nature.
    /// </summary>
    NoticeOfReadinessTendered,

    /// <summary>First mooring line ashore. The vessel is alongside but not yet secured.</summary>
    FirstLineAshore,

    /// <summary>
    /// All lines secured. The event most charter parties start laytime from -- and the one AIS
    /// systematically sees EARLY, because a vessel stops moving well before it is made fast.
    /// </summary>
    AllFast,

    /// <summary>Cargo operations began. Hoses, arms and pumps are invisible to AIS.</summary>
    CargoCommenced,

    /// <summary>Cargo operations finished.</summary>
    CargoCompleted,

    /// <summary>Operations suspended -- weather, shore stop, line clear.</summary>
    Suspended,

    /// <summary>Operations resumed.</summary>
    Resumed,

    /// <summary>Last line off. The vessel is free to move, and AIS sees it move.</summary>
    LeftBerth,

    /// <summary>Start of sea passage: the vessel departs the port area.</summary>
    StartOfSeaPassage,
}

/// <summary>
/// One line of a Statement of Facts.
///
/// The original label is kept alongside the classification, always. A classifier that silently
/// discarded "Finish Cargo Hose Connection to ship's manifold N°6 (Black Line)" in favour of the
/// enum value would throw away the detail a dispute actually turns on, and a human checking the
/// comparison needs to see the words the agent wrote.
/// </summary>
/// <param name="TimestampUtc">When it happened.</param>
/// <param name="Kind">What it means to this pipeline.</param>
/// <param name="Label">Exactly as written on the document.</param>
/// <param name="Remark">The remarks column, if any.</param>
public sealed record SofEvent(
    DateTime TimestampUtc,
    SofEventKind Kind,
    string Label,
    string? Remark = null);
