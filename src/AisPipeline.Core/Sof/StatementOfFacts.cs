namespace AisPipeline.Core.Sof;

/// <summary>
/// A Statement of Facts, as structured data.
///
/// Deliberately not parsed from the document. The sample forms this was modelled on are scans from
/// an office photocopier -- one carries two characters of text layer and page images instead -- so
/// extraction from a real SoF is an OCR problem, and OCR output must never be able to move a
/// demurrage figure without a human in between. That stage is fenced off and separate; this type
/// is its output shape, and today it is written by hand (ADR-0031).
/// </summary>
public sealed record StatementOfFacts
{
    /// <summary>MMSI, so the document can be matched to AIS. The one field a paper SoF lacks.</summary>
    public required long Mmsi { get; init; }

    public required string VesselName { get; init; }

    public required string Port { get; init; }

    /// <summary>Who prepared it -- agent or master. Relevant when two accounts disagree.</summary>
    public string? PreparedBy { get; init; }

    /// <summary>Every line, in the order the document records them.</summary>
    public required IReadOnlyList<SofEvent> Events { get; init; }

    /// <summary>The first event classified as <paramref name="kind"/>, or null.</summary>
    public SofEvent? First(SofEventKind kind) =>
        Events.Where(e => e.Kind == kind).OrderBy(e => e.TimestampUtc).FirstOrDefault();

    /// <summary>The last event classified as <paramref name="kind"/>, or null.</summary>
    public SofEvent? Last(SofEventKind kind) =>
        Events.Where(e => e.Kind == kind).OrderByDescending(e => e.TimestampUtc).FirstOrDefault();

    /// <summary>
    /// Suspension periods, paired with their resumptions in order.
    ///
    /// A suspension with no matching resumption is dropped rather than assumed to run to the end
    /// of the document: inventing an end time would fabricate excepted hours, and excepted hours
    /// are money.
    /// </summary>
    public IReadOnlyList<(SofEvent Suspended, SofEvent Resumed)> SuspensionPeriods()
    {
        var ordered = Events.OrderBy(e => e.TimestampUtc).ToList();
        var periods = new List<(SofEvent, SofEvent)>();
        SofEvent? open = null;

        foreach (var e in ordered)
        {
            if (e.Kind == SofEventKind.Suspended && open is null)
            {
                open = e;
            }
            else if (e.Kind == SofEventKind.Resumed && open is { } start)
            {
                periods.Add((start, e));
                open = null;
            }
        }

        return periods;
    }
}
