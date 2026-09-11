using AisPipeline.Core.Ingest;
using AisPipeline.Core.Quality;
using Microsoft.Extensions.Time.Testing;

namespace AisPipeline.Tests.Ingest;

/// <summary>
/// The pipeline's accounting, exercised with no database and no file. Every branch that decides
/// which bucket a row lands in is pinned here rather than inferred from one fixture's shape.
/// </summary>
public class IngestPipelineTests
{
    /// <summary>Builds a 26-field DMA row.</summary>
    private static string Row(
        string mmsi = "219005866",
        string ts = "05/09/2026 00:00:00",
        string lat = "57.321455",
        string lon = "11.126527",
        string sog = "5.0",
        string shipType = "Tanker")
    {
        var f = new string[26];
        Array.Fill(f, string.Empty);
        f[0] = ts;
        f[1] = "Class A";
        f[2] = mmsi;
        f[3] = lat;
        f[4] = lon;
        f[5] = "Under way using engine";
        f[7] = sog;
        f[13] = shipType;
        return string.Join(',', f);
    }

    private static (IngestResult Result, FakeAisStore Store) Run(
        IAisSourceRows rows, string? shipType = "Tanker")
    {
        var store = new FakeAisStore();
        var pipeline = new IngestPipeline(
            store, RuleRegistry.Default(), new IngestOptions { ShipType = shipType, BatchSize = 2 });
        return (pipeline.Run(rows.Source), store);
    }

    private sealed record IAisSourceRows(FakeAisSource Source);

    private static IAisSourceRows Rows(params string[] rows) => new(new FakeAisSource(rows));

    [Fact]
    public void EveryRowLandsInExactlyOneBucket()
    {
        var (result, _) = Run(Rows(
            Row(mmsi: "111111111"),                        // stored
            Row(mmsi: "111111111"),                        // duplicate of the above
            Row(mmsi: "222222222", shipType: "Cargo"),     // out of scope
            Row(mmsi: "111111111", lat: "91.000000", lon: "0.000000", ts: "05/09/2026 00:00:05")));

        Assert.True(result.Counters.IsBalanced);
        Assert.Equal(4, result.Counters.RowsRead);
        Assert.Equal(1, result.Counters.RowsInserted);
        Assert.Equal(1, result.Counters.RowsDuplicateInFile);
        Assert.Equal(1, result.Counters.RowsFiltered);
        Assert.Equal(1, result.Counters.RowsQuarantined);
    }

    [Fact]
    public void ScopeIsResolvedFromVesselIdentityNotFromEachRowsOwnShipType()
    {
        // ADR-0007: only the first row carries "Tanker"; the rest say "Undefined", exactly as a
        // real position row does. All four must be stored, not just the labelled one.
        var (result, _) = Run(Rows(
            Row(ts: "05/09/2026 00:00:01", shipType: "Tanker"),
            Row(ts: "05/09/2026 00:00:02", shipType: "Undefined"),
            Row(ts: "05/09/2026 00:00:03", shipType: "Undefined"),
            Row(ts: "05/09/2026 00:00:04", shipType: "Undefined")));

        Assert.Equal(4, result.Counters.RowsInserted);
        Assert.Equal(0, result.Counters.RowsFiltered);
    }

    [Fact]
    public void AnOutOfScopeRowThatAlsoFailsARuleIsFilteredNotQuarantined()
    {
        // Counting it as a reject would inflate every rule's hit rate by the share of the feed
        // that is out of scope -- and the quality report is a published number.
        var (result, store) = Run(Rows(
            Row(shipType: "Tanker"),
            Row(mmsi: "222222222", shipType: "Cargo", lat: "91.000000", lon: "0.000000")));

        Assert.Equal(1, result.Counters.RowsFiltered);
        Assert.Equal(0, result.Counters.RowsQuarantined);
        Assert.Empty(store.Quarantine);
    }

    [Fact]
    public void AnUnparseableRowIsQuarantinedBecauseItHasNoMmsiToScopeBy()
    {
        var (result, store) = Run(Rows(Row(), "not,enough,fields"));

        Assert.Equal(1, result.Counters.RowsQuarantined);
        Assert.Equal("R1", store.Quarantine[0].Row.Hit.RuleId);
    }

    [Fact]
    public void EveryQuarantinedRowCarriesTheRunThatRefusedIt()
    {
        // A refusal nobody can trace to a run is not evidence (CLAUDE.md rule 1).
        var (result, store) = Run(Rows(
            Row(),
            Row(lat: "91.000000", lon: "0.000000", ts: "05/09/2026 00:00:09")));

        Assert.NotEmpty(store.Quarantine);
        Assert.All(store.Quarantine, q => Assert.Equal(result.RunId, q.RunId));
    }

    [Fact]
    public void FinishedTimeIsReadAfterTheWorkNotAtTheStart()
    {
        // Regression guard: passing startedUtc to CompleteRun made finished_utc identical to
        // started_utc on every run, so a 57-second ingest recorded as taking no time at all.
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero));
        var store = new FakeAisStore();
        var source = new AdvancingSource(time, Row());
        var pipeline = new IngestPipeline(store, RuleRegistry.Default(), new IngestOptions(), time);

        pipeline.Run(source);

        Assert.True(store.FinishedUtc > store.StartedUtc,
            $"finished {store.FinishedUtc:O} did not advance past started {store.StartedUtc:O}");
    }

    [Fact]
    public void ARunWhosePassesDisagreeIsRefusedRatherThanReportedAsClean()
    {
        // A non-replayable source resolves scope from one sample and applies it to another, so
        // a vessel's genuine fixes get counted as out-of-scope with nothing to distinguish that
        // from a real filter. The counters would still balance, which is why this needs its own
        // check rather than relying on IsBalanced.
        var store = new FakeAisStore();
        var pipeline = new IngestPipeline(store, RuleRegistry.Default(), new IngestOptions());

        var error = Assert.Throws<InvalidOperationException>(
            () => pipeline.Run(new NonReplayableSource(Row(), Row(ts: "05/09/2026 00:00:02"))));

        Assert.Contains("not replayable", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicatesWithinTheFileAreNotAttributedToAPriorRun()
    {
        var (result, _) = Run(Rows(Row(), Row(), Row()));

        Assert.Equal(1, result.Counters.RowsInserted);
        Assert.Equal(2, result.Counters.RowsDuplicateInFile);
        Assert.Equal(0, result.Counters.RowsDuplicatePriorRun);
    }

    [Fact]
    public void FlagsAreCarriedOntoTheStoredRowRatherThanRejectingIt()
    {
        var store = new FakeAisStore();
        var pipeline = new IngestPipeline(
            store, RuleRegistry.Default(), new IngestOptions { ShipType = "Tanker" });

        var result = pipeline.Run(new FakeAisSource(Row(sog: string.Empty)));

        Assert.Equal(1, result.Counters.RowsInserted);
        Assert.Equal(0, result.Counters.RowsQuarantined);
        Assert.Equal(1, result.FeedRuleHits["R5"]);
    }

    [Fact]
    public void LimitStopsBothPassesAtTheSamePoint()
    {
        var store = new FakeAisStore();
        var pipeline = new IngestPipeline(
            store, RuleRegistry.Default(), new IngestOptions { Limit = 2 });

        var result = pipeline.Run(new FakeAisSource(
            Row(ts: "05/09/2026 00:00:01"),
            Row(ts: "05/09/2026 00:00:02"),
            Row(ts: "05/09/2026 00:00:03")));

        Assert.Equal(2, result.Counters.RowsRead);
        Assert.True(result.Counters.IsBalanced);
    }
}
