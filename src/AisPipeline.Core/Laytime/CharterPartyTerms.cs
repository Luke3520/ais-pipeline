namespace AisPipeline.Core.Laytime;

/// <summary>
/// When laytime starts counting after a Notice of Readiness is tendered.
/// </summary>
public enum LaytimeCommencement
{
    /// <summary>
    /// A fixed period after NOR, or on berthing, whichever comes first. The ASBATANKVOY shape and
    /// the common tanker case: turn time exists so the vessel cannot start the clock the instant
    /// it arrives, but berthing ends it early because work can then begin.
    /// </summary>
    TurnTimeOrBerthingWhicheverFirst,

    /// <summary>A fixed period after NOR regardless of when the vessel berths.</summary>
    TurnTimeAfterNotice,

    /// <summary>On berthing only. NOR starts nothing.</summary>
    OnBerthing,
}

/// <summary>
/// A period that does not count against laytime -- weather, a shore breakdown, shifting between
/// berths at the owner's request.
/// </summary>
/// <param name="FromUtc">Start of the excepted period.</param>
/// <param name="ToUtc">End of it.</param>
/// <param name="Reason">Why it is excepted. Appears verbatim in the statement.</param>
public sealed record LaytimeException(DateTime FromUtc, DateTime ToUtc, string Reason);

/// <summary>
/// The terms a laytime calculation is performed under, as structured data.
///
/// Deliberately not parsed from charter party prose. That is an extraction problem wearing a
/// rules-engine costume, and it is where this kind of project goes to die. Terms come in already
/// structured; what is unsupported is refused loudly rather than guessed at.
///
/// Tanker voyage charters are the tractable case and the reason this project scoped to tankers:
/// running hours SHINC -- Sundays and Holidays Included -- commonly 72 hours for load plus
/// discharge, with few exceptions. Dry bulk's weather working days and SHEX variants are a
/// different and much larger rule set.
/// </summary>
public sealed record CharterPartyTerms
{
    /// <summary>Free time allowed before demurrage begins.</summary>
    public required double LaytimeAllowedHours { get; init; }

    /// <summary>Agreed demurrage rate per running day, charged pro rata.</summary>
    public required Money DemurrageRatePerDay { get; init; }

    /// <summary>When the master tendered Notice of Readiness.</summary>
    public required DateTime NoticeOfReadinessUtc { get; init; }

    /// <summary>
    /// True when the notice time was inferred rather than taken from a document.
    ///
    /// AIS cannot observe a notice -- it is an email. A caller with no NOR to hand may substitute
    /// arrival, and that substitution has to travel with the figure rather than being annotated
    /// once at the point of printing: a statement is the unit of provenance here, and a consumer
    /// serialising it must not lose the distinction between an observed and an assumed input.
    /// </summary>
    public bool NoticeOfReadinessIsAssumed { get; init; }

    /// <summary>How commencement is determined from that notice.</summary>
    public LaytimeCommencement Commencement { get; init; } =
        LaytimeCommencement.TurnTimeOrBerthingWhicheverFirst;

    /// <summary>Turn time granted after NOR before laytime starts. Six hours on ASBATANKVOY.</summary>
    public double TurnTimeHours { get; init; } = 6.0;

    /// <summary>Periods that do not count. Overlaps are merged; order does not matter.</summary>
    public IReadOnlyList<LaytimeException> Exceptions { get; init; } = [];

    /// <summary>
    /// Whether exceptions stop applying once the vessel is on demurrage.
    ///
    /// "Once on demurrage, always on demurrage" is the default position in English law: the
    /// charterer has already used the free time it bargained for, so interruptions it would
    /// otherwise have been entitled to no longer stop the clock. Some charters contract out of it,
    /// which is why this is a term rather than a constant.
    /// </summary>
    public bool OnceOnDemurrageAlwaysOnDemurrage { get; init; } = true;
}
