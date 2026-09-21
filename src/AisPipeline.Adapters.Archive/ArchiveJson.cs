using System.Text.Json;
using AisPipeline.Core.Archive;

namespace AisPipeline.Adapters.Archive;

/// <summary>
/// Writes and reads the prune archive: the one document this pipeline cannot regenerate.
///
/// Both directions live here, deliberately. The archive was written for months by an anonymous
/// type with no reader anywhere in the codebase, which meant nothing had ever proved the file
/// could be read back -- and the only moment that matters is after the rows it describes have been
/// deleted. A single pair of functions over one set of types is what makes "we can still read it"
/// a testable claim rather than an assumption (ADR-0045).
/// </summary>
public static class ArchiveJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,

        // Reading is deliberately laxer than writing. This file is meant to be opened, and a
        // human who reformats or hand-annotates one should not find it unreadable afterwards.
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    public static string Write(ArchiveDocument document) => JsonSerializer.Serialize(document, Options);

    public static void WriteFile(string path, ArchiveDocument document) =>
        File.WriteAllText(path, Write(document));

    public static ArchiveDocument ReadFile(string path) => Read(File.ReadAllText(path), path);

    /// <param name="origin">Where the JSON came from, so an error names the file.</param>
    /// <exception cref="InvalidDataException">
    /// The document is absent, truncated, of an unknown shape, or empty of the thing it exists to
    /// carry. Every one of these is thrown rather than tolerated: a caller reading an archive is
    /// reading the last copy of something, and a partial answer is indistinguishable from a
    /// complete one once the source rows are gone.
    /// </exception>
    public static ArchiveDocument Read(string json, string origin = "<json>")
    {
        ArchiveDocument? document;

        try
        {
            document = JsonSerializer.Deserialize<ArchiveDocument>(json, Options);
        }
        catch (JsonException e)
        {
            // Truncation is the failure this catches in practice -- a prune interrupted between
            // opening the file and flushing it leaves valid-looking JSON that simply stops.
            throw new InvalidDataException($"{origin}: not a readable archive -- {e.Message}", e);
        }

        if (document is null)
        {
            throw new InvalidDataException($"{origin}: empty document");
        }

        if (document.FormatVersion is not (ArchiveDocument.CurrentFormatVersion or ArchiveDocument.BeforeVersioning))
        {
            // Forward only. A version this build has never heard of is a changed shape, and
            // deserialising it into whatever still fits would report a guess as history.
            throw new InvalidDataException(
                $"{origin}: archive format version {document.FormatVersion}, and this build reads " +
                $"versions {ArchiveDocument.BeforeVersioning} to {ArchiveDocument.CurrentFormatVersion}");
        }

        if (document.PortCalls.Count == 0)
        {
            // An archive with no calls is not a record of nothing; it is a record that failed to
            // be written. Prune refuses to delete on the strength of one.
            throw new InvalidDataException($"{origin}: archive carries no port calls");
        }

        return document;
    }
}
