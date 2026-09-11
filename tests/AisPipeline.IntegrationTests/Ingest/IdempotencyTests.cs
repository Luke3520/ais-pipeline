using AisPipeline.Adapters.Csv;
using AisPipeline.Adapters.Sqlite;
using AisPipeline.Core.Ingest;
using AisPipeline.Core.Quality;
using Microsoft.Data.Sqlite;

namespace AisPipeline.IntegrationTests.Ingest;

/// <summary>
/// The headline guarantee: running the same file twice changes nothing and says so.
///
/// These run the real pipeline through the real CSV reader into a real SQLite file, because
/// the guarantee is enforced by a database constraint rather than by a pure function -- there
/// is no unit test that could establish it (ADR-0005).
/// </summary>
public sealed class IdempotencyTests : IDisposable
{
    private readonly string _database = Path.Combine(
        Path.GetTempPath(), $"ais-idem-{Guid.NewGuid():N}.db");

    private static string FixturePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AisPipeline.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "fixtures", "sample.csv");
    }

    private IngestResult Ingest(string? shipType = "Tanker")
    {
        using var store = new SqliteAisStore(_database);
        var pipeline = new IngestPipeline(
            store, RuleRegistry.Default(), new IngestOptions { ShipType = shipType });
        return pipeline.Run(new DmaCsvSource(FixturePath()));
    }

    private long Count(string table)
    {
        using var connection = new SqliteConnection($"Data Source={_database}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}";
        return (long)command.ExecuteScalar()!;
    }

    [Fact]
    public void SecondIngestOfTheSameFileInsertsNothing()
    {
        var first = Ingest();
        Assert.True(first.Counters.RowsInserted > 0, "first run stored nothing; fixture is wrong");

        var second = Ingest();

        Assert.Equal(0, second.Counters.RowsInserted);
        Assert.Equal(first.Counters.RowsInserted, second.Counters.RowsDuplicatePriorRun);
    }

    [Fact]
    public void SecondIngestLeavesEveryTableTheSameSize()
    {
        Ingest();
        var positions = Count("position_report");
        var quarantined = Count("quarantine");
        var vessels = Count("vessel");

        Ingest();

        Assert.Equal(positions, Count("position_report"));

        // quarantine is the side door: without UNIQUE (source_file, source_line, rule_id) the
        // positions stay flat while the refusals double, and the guarantee is false while
        // appearing to hold (ADR-0006).
        Assert.Equal(quarantined, Count("quarantine"));
        Assert.Equal(vessels, Count("vessel"));
    }

    [Fact]
    public void EveryRowReadIsAccountedForOnBothRuns()
    {
        // The accounting identity. A row that vanishes without landing in a bucket is a silent
        // loss -- blocking class 1 -- and would show here as an unbalanced run.
        var first = Ingest();
        var second = Ingest();

        Assert.True(first.Counters.IsBalanced,
            $"run 1: read {first.Counters.RowsRead}, accounted {first.Counters.Accounted}");
        Assert.True(second.Counters.IsBalanced,
            $"run 2: read {second.Counters.RowsRead}, accounted {second.Counters.Accounted}");
    }

    [Fact]
    public void BothRunsReadTheSameNumberOfRows()
    {
        var first = Ingest();
        var second = Ingest();

        Assert.Equal(first.Counters.RowsRead, second.Counters.RowsRead);
    }

    [Fact]
    public void EachRunIsRecordedSeparatelyEvenThoughItStoredNothing()
    {
        // Idempotent ingest does not mean an invisible run. The audit trail must show that the
        // file was processed twice, with the second run's counters explaining why it was a no-op.
        Ingest();
        Ingest();

        Assert.Equal(2, Count("ingest_run"));
    }

    [Fact]
    public void EveryStoredPositionResolvesToItsIngestRunAndSourceLine()
    {
        // Provenance: a number you cannot trace is a number you cannot trust. A row whose
        // ingest_run_id does not resolve is blocking class 3.
        Ingest();

        using var connection = new SqliteConnection($"Data Source={_database}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM position_report p
            LEFT JOIN ingest_run r ON r.id = p.ingest_run_id
            WHERE r.id IS NULL OR p.source_line <= 0;
            """;

        Assert.Equal(0L, (long)command.ExecuteScalar()!);
    }

    [Fact]
    public void EveryQuarantinedRowKeepsTheEvidenceThatJustifiesTheRefusal()
    {
        Ingest();

        using var connection = new SqliteConnection($"Data Source={_database}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM quarantine
            WHERE rule_id = '' OR raw_snippet = '' OR source_line <= 0 OR detail IS NULL;
            """;

        Assert.Equal(0L, (long)command.ExecuteScalar()!);
    }

    [Fact]
    public void ScopeGuardFiltersByVesselIdentityNotByEachRowsOwnShipType()
    {
        // ADR-0007. Static data rides only on message-type-5 rows, so a tanker's position rows
        // carry ship type "Undefined". If the guard read each row's own field, those fixes
        // would be dropped -- so a scoped run must store MORE rows than the fixture has rows
        // literally labelled Tanker.
        var scoped = Ingest();

        var literallyLabelled = File.ReadLines(FixturePath())
            .Skip(1)
            .Count(l => l.Split(',') is { Length: 26 } f && f[13] == "Tanker");

        Assert.True(
            scoped.Counters.RowsInserted + scoped.Counters.RowsDuplicateInFile > literallyLabelled,
            $"stored {scoped.Counters.RowsInserted + scoped.Counters.RowsDuplicateInFile} rows for " +
            $"in-scope vessels but only {literallyLabelled} rows carry the Tanker label; " +
            "the scope guard appears to be filtering per row");
    }

    [Fact]
    public void EveryQuarantinedRowResolvesToTheRunThatRefusedIt()
    {
        // CLAUDE.md rule 1 says every stored record traces to an ingest run, and a refusal is a
        // stored record. Without the run id, two runs over the same filename leave every
        // quarantine row reachable from either -- permanently, because INSERT OR IGNORE makes
        // the second write a silent no-op.
        Ingest();
        Ingest();

        using var connection = new SqliteConnection($"Data Source={_database}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM quarantine q
            LEFT JOIN ingest_run r ON r.id = q.ingest_run_id
            WHERE r.id IS NULL;
            """;

        Assert.Equal(0L, (long)command.ExecuteScalar()!);
    }

    [Fact]
    public void AReRefusedRowStaysAttributedToTheFirstRunThatRefusedIt()
    {
        // Matches how position_report keeps its original ingest_run_id: first ingest owns the
        // row. The point is that the attribution is unambiguous, not which run wins.
        Ingest();

        using var connection = new SqliteConnection($"Data Source={_database}");
        connection.Open();
        using var before = connection.CreateCommand();
        before.CommandText = "SELECT DISTINCT ingest_run_id FROM quarantine";
        var firstRun = (long)before.ExecuteScalar()!;

        Ingest();

        using var after = connection.CreateCommand();
        after.CommandText = "SELECT COUNT(DISTINCT ingest_run_id) FROM quarantine";
        Assert.Equal(1L, (long)after.ExecuteScalar()!);

        using var which = connection.CreateCommand();
        which.CommandText = "SELECT DISTINCT ingest_run_id FROM quarantine";
        Assert.Equal(firstRun, (long)which.ExecuteScalar()!);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            File.Delete(_database + suffix);
        }
    }
}
