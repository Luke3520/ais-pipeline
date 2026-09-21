namespace AisPipeline.Core.Archive;

/// <summary>
/// What prune wrote down before it deleted anything.
///
/// Past the cutoff this document is the only record that a port call ever happened: the fixes it
/// was derived from are gone, and rule 5 means it cannot be rebuilt from anything else. That makes
/// it the one artefact this pipeline produces which is NOT reproducible, so its shape is declared
/// here, in the core, rather than emerging from whatever the CLI happened to serialise -- and it is
/// read back through these same types, so a writer and a reader cannot drift apart (ADR-0045).
/// </summary>
public sealed record ArchiveDocument
{
    /// <summary>
    /// The shape of this document, so a reader can refuse one it does not understand.
    ///
    /// An archive outlives the code that wrote it -- that is its entire purpose -- so a reader two
    /// years from now meets a file it cannot ask any questions about. Refusing an unknown version
    /// by name beats deserialising a changed shape into whatever happens to still fit and
    /// reporting the result as history.
    ///
    /// The one field here that is NOT `required`, and deliberately: an archive written before
    /// versioning existed has no such field, and refusing it would make the first archives this
    /// code inherits unreadable by the very tool built to prove archives are readable. Absent
    /// means <see cref="BeforeVersioning"/>, which is a shape this build knows -- it is this one,
    /// minus this field.
    /// </summary>
    public int FormatVersion { get; init; } = BeforeVersioning;

    /// <summary>The version this code writes, and the newest one it reads.</summary>
    public const int CurrentFormatVersion = 1;

    /// <summary>
    /// What an archive written before the format carried a version reports as.
    ///
    /// Not a guess. The pre-versioning writer emitted the same field names, in the same camel
    /// case, for every value this document holds (ADR-0045's prune, superseded by ADR-0046); the
    /// version is the only thing it lacked. So one of these reads completely, and is not the same
    /// event as a file that will not parse.
    /// </summary>
    public const int BeforeVersioning = 0;

    public required DateTime ArchivedUtc { get; init; }

    /// <summary>Everything arriving before this instant was archived and then pruned.</summary>
    public required DateTime CutoffUtc { get; init; }

    /// <summary>
    /// The source files the store had ingested when this archive was written, distinct, in run
    /// order.
    ///
    /// The whole store, not only the days the archived calls came from: a run records how many
    /// rows it read, not the window it covered, so narrowing this list would be a guess dressed
    /// as provenance. It names the feed this history came out of, which is what rule 1 leaves
    /// once the rows themselves are gone.
    /// </summary>
    public required IReadOnlyList<string> SourceFiles { get; init; }

    public required IReadOnlyList<ArchivedPortCall> PortCalls { get; init; }
}

/// <summary>One port call as it stood when it was last derivable.</summary>
public sealed record ArchivedPortCall
{
    /// <summary>
    /// The id the call held in the store it was pruned from.
    ///
    /// Kept for tracing back to logs and exports written while the call still existed, and for
    /// nothing else. Detection renumbers on every rebuild, so this id will not match anything in
    /// the store after the next `detect` -- it is a historical reference, not a key.
    /// </summary>
    public required long Id { get; init; }

    public required long Mmsi { get; init; }
    public required DateTime ArrivedUtc { get; init; }
    public required DateTime DepartedUtc { get; init; }
    public required double WaitingHours { get; init; }
    public required double WorkingHours { get; init; }

    /// <summary>Phases whose geometry could not be trusted: neither waiting nor working.</summary>
    public required double UnclassifiedHours { get; init; }

    /// <summary>
    /// Whether the call's true extent was known when it was archived.
    ///
    /// A false here is permanent in a way it never was in the store. An incomplete call could
    /// always be completed by ingesting the days on either side of it; once the fixes are pruned,
    /// no later ingest can finish it.
    /// </summary>
    public required bool IsComplete { get; init; }

    public required string? PortName { get; init; }
    public required string? PortCountry { get; init; }

    /// <summary>
    /// How far the call's centroid sat from that port's reference point, in nautical miles.
    ///
    /// Travels with the name here for the same reason it does everywhere else: a World Port Index
    /// record is one nominal point near the harbour entrance, and a name without its distance
    /// claims more than the data supports (ADR-0034).
    /// </summary>
    public required double? PortDistanceNm { get; init; }

    public required IReadOnlyList<ArchivedPhase> Phases { get; init; }
}

/// <summary>One stop within an archived call, in the order it happened.</summary>
public sealed record ArchivedPhase
{
    public required int Sequence { get; init; }

    /// <summary>Anchorage, Berth, or Unknown -- what the drift geometry decided.</summary>
    public required string Phase { get; init; }

    public required DateTime StartedUtc { get; init; }
    public required DateTime EndedUtc { get; init; }

    /// <summary>
    /// The observed span between the stop's first and last fix: always a lower bound.
    ///
    /// The observed figure rather than the nullable <c>DurationHours</c>, because a null in a
    /// document that cannot be regenerated discards the bound along with the doubt. The flag
    /// beside it carries the doubt, which is the same division of labour the store uses.
    /// </summary>
    public required double ObservedDurationHours { get; init; }

    public required bool IsComplete { get; init; }

    /// <summary>What the vessel's own transponder claimed it was doing.</summary>
    public required string? ReportedStatus { get; init; }

    /// <summary>
    /// Whether that claim agreed with the vessel's own speed (R10).
    ///
    /// Both readings were stored and neither won (rule 4), so both survive into the archive.
    /// Dropping the disagreement here would resolve at deletion time a conflict the pipeline
    /// deliberately refused to resolve at every earlier stage.
    /// </summary>
    public required bool StatusAgrees { get; init; }
}
