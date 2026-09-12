using AisPipeline.Adapters.Csv;
using AisPipeline.Adapters.Sql;
using AisPipeline.Core.Domain;
using AisPipeline.Core.Ingest;
using AisPipeline.Core.Quality;
using AisPipeline.Core.Query;
using AisPipeline.IntegrationTests.Harness;

namespace AisPipeline.IntegrationTests.Query;

/// <summary>
/// A figure whose validity depends on another field is exposed as null when that field says it is
/// meaningless, rather than as a number the caller must remember to qualify.
///
/// The convention is not new: rule R5 stores unavailable speed as null rather than zero, precisely
/// so detection cannot treat "not reported" as "stationary". The same reasoning applies to a
/// censored duration and to drift measured over one surviving fix.
/// </summary>
public class QualifiedFigureTests
{
    private const long Mmsi = 219000998;

    private static string FixturePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AisPipeline.slnx")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir!.FullName, "fixtures", "sample.csv");
    }

    /// <summary>Writes one stop with the completeness and geometry flags under test.</summary>
    private static void Seed(IStoreHarness harness, bool complete, int reliableFixes)
    {
        long anchorId;
        long mmsi;

        using (var store = harness.Create())
        {
            new IngestPipeline(store, RuleRegistry.Default(),
                new IngestOptions { ShipType = "Tanker" })
                .Run(new DmaCsvSource(FixturePath()));
        }

        using (var read = new SqlAisQueries(harness.ConnectionFactory, harness.Dialect))
        {
            mmsi = read.ListVessels("Tanker", 1).Single().Mmsi;
        }

        // Any real position id, so the synthetic stop below points at a row that exists. Read
        // straight from the table rather than through a query method: these tests need an id, not
        // a window, and the read side should not carry a method whose only caller is a fixture.
        anchorId = harness.Scalar($"SELECT MIN(id) FROM position_report WHERE mmsi = {mmsi}");

        {
        }

        var started = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var stop = new StopEvent
        {
            Mmsi = Mmsi,
            StartedUtc = started,
            EndedUtc = started.AddHours(6),
            CentroidLatitude = 56.0,
            CentroidLongitude = 10.0,
            MaxDriftNm = 0.0,
            FixCount = 200,
            ReliableFixCount = reliableFixes,
            ReportedStatus = "Moored",
            StatusAgrees = true,
            IsComplete = complete,
            FirstPositionId = anchorId,
            LastPositionId = anchorId,
        };

        using var write = harness.Create();
        write.ReplaceDetections([new PortCall
        {
            Mmsi = Mmsi,
            Phases = [new PortCallPhase(0, StopPhase.Berth, stop)],
        }]);
    }

    private static StoredStop Read(IStoreHarness harness)
    {
        using var queries = new SqlAisQueries(harness.ConnectionFactory, harness.Dialect);
        return queries.ListStops(new StopFilter { Mmsi = Mmsi, Limit = 10 }).Single();
    }

    [Theory]
    [ClassData(typeof(StoreHarnesses))]
    public void AnIncompleteStopHasNoDurationButKeepsItsLowerBound(Func<IStoreHarness> make)
    {
        // Roughly a third of tankers are stationary across any given midnight, so a censored stop
        // is the common case. A consumer summing durations must not silently count one as measured.
        using var harness = make();
        Seed(harness, complete: false, reliableFixes: 200);

        var stop = Read(harness);

        Assert.Null(stop.DurationHours);
        Assert.Equal(6.0, stop.ObservedDurationHours, 3);
    }

    [Theory]
    [ClassData(typeof(StoreHarnesses))]
    public void ACompleteStopReportsItsDuration(Func<IStoreHarness> make)
    {
        using var harness = make();
        Seed(harness, complete: true, reliableFixes: 200);

        var stop = Read(harness);

        Assert.Equal(6.0, stop.DurationHours!.Value, 3);
        Assert.Equal(stop.ObservedDurationHours, stop.DurationHours!.Value);
    }

    [Theory]
    [ClassData(typeof(StoreHarnesses))]
    public void DriftIsNullWhenTooFewFixesSurvivedToMeasureIt(Func<IStoreHarness> make)
    {
        // With one reliable fix the centroid IS that fix, so drift computes to exactly 0.0 -- the
        // most confident possible berth reading, on the stop most likely to have been drifting.
        using var harness = make();
        Seed(harness, complete: true, reliableFixes: 1);

        var stop = Read(harness);

        Assert.False(stop.GeometryTrustworthy);
        Assert.Null(stop.MaxDriftNm);
        Assert.Equal(0.0, stop.ObservedMaxDriftNm);
    }

    [Theory]
    [ClassData(typeof(StoreHarnesses))]
    public void DriftIsReportedWhenTheGeometrySurvivedIntact(Func<IStoreHarness> make)
    {
        using var harness = make();
        Seed(harness, complete: true, reliableFixes: 200);

        var stop = Read(harness);

        Assert.True(stop.GeometryTrustworthy);
        Assert.NotNull(stop.MaxDriftNm);
    }
}
