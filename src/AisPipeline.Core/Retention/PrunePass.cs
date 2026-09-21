using AisPipeline.Core.Archive;
using AisPipeline.Core.Ports;
using AisPipeline.Core.Query;

namespace AisPipeline.Core.Retention;

/// <summary>What a prune did, or why it declined to do anything.</summary>
public sealed record PruneResult
{
    public required bool Pruned { get; init; }

    /// <summary>Why nothing was removed, or null when something was.</summary>
    public required string? RefusedBecause { get; init; }

    public required long FixesRemoved { get; init; }
    public required int PortCallsArchived { get; init; }

    /// <summary>Where the archive went, or null when there was nothing to archive.</summary>
    public required string? ArchivePath { get; init; }

    public static PruneResult Refused(string because) => new()
    {
        Pruned = false,
        RefusedBecause = because,
        FixesRemoved = 0,
        PortCallsArchived = 0,
        ArchivePath = null,
    };
}

/// <summary>
/// Archives the port calls a cutoff is about to make underivable, proves the archive can be read,
/// and only then deletes.
///
/// The order is the whole point, and it lives in Core so it can be asserted without a database:
/// the store is never asked to prune unless a document came back from the archive carrying every
/// call that is about to stop existing. An earlier version checked that the file was non-empty and
/// deleted on the strength of it, which passes for a truncated write (ADR-0046).
///
/// Fetching is the caller's job, as it is for the export (ADR-0039). This pass is given what will
/// be lost and decides what happens to it.
/// </summary>
public sealed class PrunePass
{
    private readonly IAisStore _store;
    private readonly IPortCallArchive _archive;

    /// <summary>
    /// Recorded as the archive path when a prune had no port call to archive.
    ///
    /// `retention_event.archive_path` is NOT NULL and a reader of a pruned store follows it to
    /// find what used to be there. A filename that was never written is worse than a sentence
    /// saying no file exists.
    /// </summary>
    public const string NoArchiveWritten = "(none written: no port call predates the cutoff)";

    public PrunePass(IAisStore store, IPortCallArchive archive)
    {
        _store = store;
        _archive = archive;
    }

    /// <param name="losing">The calls that will no longer be derivable after the cutoff.</param>
    public PruneResult Run(
        DateTime cutoffUtc,
        DateTime archivedUtc,
        IReadOnlyList<StoredPortCall> losing,
        IReadOnlyList<StoredRun> runs,
        IReadOnlyList<(StoredPhase Phase, StoredStop Stop)> phases)
    {
        if (losing.Count == 0)
        {
            // No history to preserve, so no archive to write -- and deliberately not an empty
            // one. A document carrying no calls is refused on read, which is how a failed write
            // is caught, so writing one here would block a prune that has nothing to lose. Fixes
            // with no derived call behind them are still fixes, and still past the time bar.
            return Delete(cutoffUtc, 0, NoArchiveWritten);
        }

        var path = _archive.Write(ArchiveBuilder.Build(archivedUtc, cutoffUtc, runs, losing, phases));

        ArchiveDocument readBack;

        try
        {
            readBack = _archive.Read(path);
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            return PruneResult.Refused($"{e.Message}; nothing removed");
        }

        if (readBack.PortCalls.Count != losing.Count)
        {
            // The count is the cheapest question whose wrong answer means the archive does not
            // cover what is about to be deleted. Asked after the parse, because a document that
            // parsed is not yet a document that is complete.
            return PruneResult.Refused(
                $"archive at {path} read back {readBack.PortCalls.Count} call(s) of {losing.Count}; " +
                "nothing removed");
        }

        return Delete(cutoffUtc, losing.Count, path);
    }

    private PruneResult Delete(DateTime cutoffUtc, int archived, string archivePath) => new()
    {
        Pruned = true,
        RefusedBecause = null,
        FixesRemoved = _store.PruneBefore(cutoffUtc, archived, archivePath),
        PortCallsArchived = archived,
        ArchivePath = archivePath == NoArchiveWritten ? null : archivePath,
    };
}
