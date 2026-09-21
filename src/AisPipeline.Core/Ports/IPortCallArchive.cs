using AisPipeline.Core.Archive;

namespace AisPipeline.Core.Ports;

/// <summary>
/// Somewhere an archive can be written and read back.
///
/// A port rather than a file path in the middle of the prune, because the decision that matters
/// -- delete nothing unless the archive reads back intact -- is a domain rule, and a domain rule
/// that can only be exercised by deleting three million rows off a real disk is a rule nobody
/// tests (ADR-0046). Core states it here; the adapter supplies JSON and a filesystem.
/// </summary>
public interface IPortCallArchive
{
    /// <summary>Writes the document, and returns where it went.</summary>
    string Write(ArchiveDocument document);

    /// <summary>
    /// Reads one back.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// Absent, truncated, of an unknown shape, or carrying no calls. Refusing is the contract: the
    /// caller is about to delete the rows this document describes, and an implementation that
    /// returned a partial answer here would make that deletion silent.
    /// </exception>
    ArchiveDocument Read(string path);
}
