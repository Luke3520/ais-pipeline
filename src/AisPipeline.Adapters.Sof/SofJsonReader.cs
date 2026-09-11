using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AisPipeline.Core.Sof;

namespace AisPipeline.Adapters.Sof;

/// <summary>
/// Reads a Statement of Facts from structured JSON.
///
/// Structured, not extracted. The real documents this was modelled on are scans from an office
/// photocopier, so ingesting one is an OCR problem — and OCR output must never move a demurrage
/// figure without a human in between. That stage is fenced off; this reads its output, and today
/// that output is typed by hand (ADR-0031).
/// </summary>
public static class SofJsonReader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static StatementOfFacts ReadFile(string path) => Read(File.ReadAllText(path), path);

    /// <param name="origin">Where the JSON came from, so an error names the file.</param>
    public static StatementOfFacts Read(string json, string origin = "<json>")
    {
        var document = JsonSerializer.Deserialize<SofDocument>(json, Options)
            ?? throw new InvalidDataException($"{origin}: empty document");

        if (document.Mmsi <= 0)
        {
            // The one field a paper SoF does not carry, and the one that makes it matchable to
            // AIS at all. Refused rather than defaulted: a statement attached to the wrong vessel
            // would produce a confident comparison against someone else's voyage.
            throw new InvalidDataException(
                $"{origin}: 'mmsi' is required — it is what matches this document to AIS");
        }

        if (document.Events is not { Count: > 0 })
        {
            throw new InvalidDataException($"{origin}: a statement with no events states nothing");
        }

        var events = document.Events
            .Select((e, i) => ReadEvent(e, i, origin))
            .OrderBy(e => e.TimestampUtc)
            .ToList();

        return new StatementOfFacts
        {
            Mmsi = document.Mmsi,
            VesselName = document.VesselName ?? "",
            Port = document.Port ?? "",
            PreparedBy = document.PreparedBy,
            Events = events,
        };
    }

    private static SofEvent ReadEvent(SofEventDocument e, int index, string origin)
    {
        if (string.IsNullOrWhiteSpace(e.Label))
        {
            throw new InvalidDataException($"{origin}: event {index} has no label");
        }

        if (!DateTime.TryParse(e.TimestampUtc, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var timestamp))
        {
            throw new InvalidDataException(
                $"{origin}: event {index} ('{e.Label}') has an unreadable time '{e.TimestampUtc}'");
        }

        // An explicit kind overrides the classifier. Agents write things no pattern anticipates,
        // and a human who knows what a line means must be able to say so rather than argue with
        // the matcher.
        var kind = e.Kind is { } named && Enum.TryParse<SofEventKind>(named, ignoreCase: true, out var parsed)
            ? parsed
            : SofEventClassifier.Classify(e.Label);

        return new SofEvent(timestamp, kind, e.Label.Trim(), e.Remark);
    }

    private sealed record SofDocument
    {
        public long Mmsi { get; init; }
        public string? VesselName { get; init; }
        public string? Port { get; init; }
        public string? PreparedBy { get; init; }
        public IReadOnlyList<SofEventDocument>? Events { get; init; }
    }

    private sealed record SofEventDocument
    {
        public string? TimestampUtc { get; init; }
        public string? Label { get; init; }

        /// <summary>Optional explicit classification, overriding the label matcher.</summary>
        public string? Kind { get; init; }

        public string? Remark { get; init; }
    }
}
