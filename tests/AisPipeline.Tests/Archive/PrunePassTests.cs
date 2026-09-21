using AisPipeline.Core.Archive;
using AisPipeline.Core.Ports;
using AisPipeline.Core.Query;
using AisPipeline.Core.Retention;
using AisPipeline.Tests.Ingest;

namespace AisPipeline.Tests.Archive;

/// <summary>
/// The rule that stands between a prune and three million deleted rows: nothing goes until the
/// archive has been read back and found to carry every call that is about to stop existing.
///
/// Asserted here, with no database and no disk, because the version of this rule that lived in
/// the CLI could only be exercised by actually destroying data -- so it never was, and "the file
/// is non-empty" passed for verification for as long as it existed (ADR-0046).
/// </summary>
public class PrunePassTests
{
    private static readonly DateTime Cutoff = new(2026, 9, 5, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ArchivedAt = new(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>An archive that holds documents in memory and can be told to misbehave.</summary>
    private sealed class FakeArchive : IPortCallArchive
    {
        private readonly Dictionary<string, ArchiveDocument> _written = [];

        /// <summary>Thrown instead of returning, standing in for a truncated or absent file.</summary>
        public Exception? ReadThrows { get; set; }

        /// <summary>Returned instead of what was written, standing in for a partial write.</summary>
        public ArchiveDocument? ReadReturns { get; set; }

        public List<ArchiveDocument> Writes { get; } = [];

        public string Write(ArchiveDocument document)
        {
            Writes.Add(document);
            var path = $"archive/port-calls-before-{document.CutoffUtc:yyyyMMddTHHmmss}Z.json";
            _written[path] = document;
            return path;
        }

        public ArchiveDocument Read(string path) =>
            ReadThrows is { } failure
                ? throw failure
                : ReadReturns ?? _written[path];
    }

    private static StoredPortCall Call(long id, DateTime arrived) => new()
    {
        Id = id,
        Mmsi = 200000000 + id,
        ArrivedUtc = arrived,
        DepartedUtc = arrived.AddHours(8),
        WaitingHours = 3.0,
        WorkingHours = 5.0,
        UnclassifiedHours = 0.0,
        IsComplete = true,
        PortName = "Arhus",
        PortCountry = "DK",
        PortDistanceNm = 0.5,
    };

    private static IReadOnlyList<StoredPortCall> TwoCalls() =>
        [Call(1, Cutoff.AddDays(-2)), Call(2, Cutoff.AddDays(-1))];

    private static PruneResult Run(
        FakeAisStore store,
        FakeArchive archive,
        IReadOnlyList<StoredPortCall>? losing = null) =>
        new PrunePass(store, archive).Run(
            Cutoff,
            ArchivedAt,
            losing ?? TwoCalls(),
            [new StoredRun { Id = 1, SourceFile = "day-1.zip", RowsRead = 10, RowsInserted = 5 }],
            []);

    [Fact]
    public void Archives_before_it_deletes()
    {
        var store = new FakeAisStore();
        var archive = new FakeArchive();

        var result = Run(store, archive);

        Assert.True(result.Pruned);
        Assert.Single(archive.Writes);
        Assert.Single(store.Prunes);
        Assert.Equal(2, result.PortCallsArchived);
    }

    [Fact]
    public void Refuses_to_delete_anything_when_the_archive_will_not_read_back()
    {
        // The failure the old non-empty check passed: a write interrupted between opening the
        // file and flushing it. The archive is on disk and looks plausible; it does not parse.
        var store = new FakeAisStore();
        var archive = new FakeArchive { ReadThrows = new InvalidDataException("truncated.json: not a readable archive") };

        var result = Run(store, archive);

        Assert.False(result.Pruned);
        Assert.Empty(store.Prunes);
        Assert.Equal(0, result.FixesRemoved);
    }

    [Fact]
    public void Refuses_to_delete_anything_when_the_archive_is_unreachable()
    {
        // A full disk, a directory that disappeared, a permission change between write and read.
        var store = new FakeAisStore();
        var archive = new FakeArchive { ReadThrows = new IOException("no space left on device") };

        Assert.False(Run(store, archive).Pruned);
        Assert.Empty(store.Prunes);
    }

    [Fact]
    public void Refuses_when_the_archive_came_back_short()
    {
        // Parsed, and still wrong. A document that reads cleanly but covers one of the two calls
        // about to be deleted would leave the other one nowhere at all.
        var store = new FakeAisStore();
        var archive = new FakeArchive();
        archive.ReadReturns = ArchiveBuilder.Build(ArchivedAt, Cutoff, [], [Call(1, Cutoff.AddDays(-2))], []);

        var result = Run(store, archive);

        Assert.False(result.Pruned);
        Assert.Empty(store.Prunes);
        Assert.Contains("1 call(s) of 2", result.RefusedBecause);
    }

    [Fact]
    public void Refuses_when_the_archive_came_back_long()
    {
        // The other side of the same inequality. A stale read -- a leftover document from an
        // earlier prune, or a path collision -- parses cleanly and carries MORE calls than are
        // about to be lost, which means it is not the archive of this prune and cannot be
        // treated as one. The guard refuses on any mismatch, not only on a shortfall.
        var store = new FakeAisStore();
        var archive = new FakeArchive();
        archive.ReadReturns = ArchiveBuilder.Build(
            ArchivedAt,
            Cutoff,
            [],
            [Call(1, Cutoff.AddDays(-3)), Call(2, Cutoff.AddDays(-2)), Call(3, Cutoff.AddDays(-1))],
            []);

        var result = Run(store, archive);

        Assert.False(result.Pruned);
        Assert.Empty(store.Prunes);
        Assert.Contains("3 call(s) of 2", result.RefusedBecause);
    }

    [Fact]
    public void The_refusal_says_why()
    {
        // It is printed to a human who has just been told their prune did not happen, and the
        // next thing they will do is decide whether to force it.
        var store = new FakeAisStore();
        var archive = new FakeArchive { ReadThrows = new InvalidDataException("archive.json: carries no port calls") };

        Assert.Contains("carries no port calls", Run(store, archive).RefusedBecause);
        Assert.Contains("nothing removed", Run(store, archive).RefusedBecause);
    }

    [Fact]
    public void Hands_the_archive_path_to_the_store_so_a_pruned_store_can_name_it()
    {
        // Rule 2: a deliberate deletion is still a disappearance, and retention_event is where a
        // reader of the pruned store finds out where the rows went.
        var store = new FakeAisStore();
        var archive = new FakeArchive();

        var result = Run(store, archive);

        Assert.Equal(result.ArchivePath, store.Prunes[0].ArchivePath);
        Assert.Contains("port-calls-before-", store.Prunes[0].ArchivePath);
    }

    [Fact]
    public void Prunes_fixes_when_no_port_call_is_old_enough_to_archive()
    {
        // Old fixes with no derived call behind them are still fixes, and still past the time
        // bar. Writing an empty archive here would be refused on read and would block this.
        var store = new FakeAisStore();
        var archive = new FakeArchive();

        var result = Run(store, archive, losing: []);

        Assert.True(result.Pruned);
        Assert.Empty(archive.Writes);
        Assert.Single(store.Prunes);
        Assert.Equal(0, store.Prunes[0].PortCallsArchived);
    }

    [Fact]
    public void Says_in_the_retention_record_that_no_archive_was_written()
    {
        // The column is NOT NULL and a reader follows it. A filename that was never written is
        // worse than a sentence saying no file exists.
        var store = new FakeAisStore();

        var result = Run(store, new FakeArchive(), losing: []);

        Assert.Null(result.ArchivePath);
        Assert.Equal(PrunePass.NoArchiveWritten, store.Prunes[0].ArchivePath);
    }

    [Fact]
    public void Reports_what_the_store_removed()
    {
        var store = new FakeAisStore { FixesRemovedPerPrune = 3_433_059 };

        Assert.Equal(3_433_059, Run(store, new FakeArchive()).FixesRemoved);
    }

    [Fact]
    public void Prunes_at_the_cutoff_it_was_given()
    {
        // Never rounded and never adjusted. ADR-0045 chose a design where nothing survives across
        // the cutoff precisely so that it does not have to be moved to avoid straddling a call.
        var store = new FakeAisStore();

        Run(store, new FakeArchive());

        Assert.Equal(Cutoff, store.Prunes[0].CutoffUtc);
    }
}
