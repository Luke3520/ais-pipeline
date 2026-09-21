using AisPipeline.Adapters.Archive;
using AisPipeline.Core.Archive;
using AisPipeline.Core.Ports;
using AisPipeline.Core.Query;

namespace AisPipeline.IntegrationTests.Archive;

/// <summary>
/// The archive port over a real directory. Core decides that a prune reads its archive back before
/// deleting (ADR-0046); this is the half of that promise which touches a filesystem.
/// </summary>
public class JsonFileArchiveTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"ais-archive-{Guid.NewGuid():N}");

    private static readonly DateTime Cutoff = new(2026, 9, 5, 11, 57, 4, DateTimeKind.Utc);

    private static ArchiveDocument Document() =>
        ArchiveBuilder.Build(
            Cutoff.AddDays(16),
            Cutoff,
            [new StoredRun { Id = 1, SourceFile = "aisdk-2026-09-01.zip", RowsRead = 10, RowsInserted = 5 }],
            [new StoredPortCall
            {
                Id = 94,
                Mmsi = 230687000,
                ArrivedUtc = Cutoff.AddDays(-4),
                DepartedUtc = Cutoff.AddHours(-1),
                WaitingHours = 100.2,
                WorkingHours = 0.0,
                UnclassifiedHours = 0.0,
                IsComplete = false,
                PortName = "Kerteminde",
                PortCountry = "DK",
                PortDistanceNm = 6.7,
            }],
            []);

    [Fact]
    public void WritesReadsAndCreatesItsDirectory()
    {
        IPortCallArchive archive = new JsonFileArchive(_directory);

        var path = archive.Write(Document());

        Assert.True(File.Exists(path));
        Assert.Equal(230687000, archive.Read(path).PortCalls[0].Mmsi);
    }

    [Fact]
    public void NamesTheFileForTheCutoffSoPrunesDoNotOverwriteEachOther()
    {
        // A directory of these is a history of prunes. One name for all of them would leave only
        // the most recent, and the records it replaced cannot be regenerated.
        var path = new JsonFileArchive(_directory).Write(Document());

        Assert.Equal("port-calls-before-20260905T115704Z.json", Path.GetFileName(path));
    }

    [Fact]
    public void ReadingAnAbsentArchiveThrowsRatherThanReturningNothing()
    {
        // Caught by the prune, which then removes nothing. An empty document here would read as
        // an archive that legitimately holds no calls.
        IPortCallArchive archive = new JsonFileArchive(_directory);

        Assert.ThrowsAny<IOException>(() => archive.Read(Path.Combine(_directory, "not-here.json")));
    }

    [Fact]
    public void ATruncatedArchiveOnDiskIsRefused()
    {
        // End to end through the port: the bytes are really on disk, really half-written, and
        // the read really fails. This is the shape that made the old non-empty check useless.
        IPortCallArchive archive = new JsonFileArchive(_directory);
        var path = archive.Write(Document());
        var whole = File.ReadAllText(path);
        File.WriteAllText(path, whole[..(whole.Length / 2)]);

        Assert.Throws<InvalidDataException>(() => archive.Read(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }
}
