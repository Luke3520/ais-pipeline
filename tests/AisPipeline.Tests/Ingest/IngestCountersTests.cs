using AisPipeline.Core.Ingest;

namespace AisPipeline.Tests.Ingest;

public class IngestCountersTests
{
    [Fact]
    public void BalancedWhenEveryRowReadLandsInExactlyOneBucket()
    {
        var counters = new IngestCounters
        {
            RowsRead = 100,
            RowsFiltered = 20,
            RowsInserted = 50,
            RowsDuplicateInFile = 20,
            RowsDuplicatePriorRun = 5,
            RowsQuarantined = 5,
        };

        Assert.True(counters.IsBalanced);
        Assert.Equal(100, counters.Accounted);
    }

    [Fact]
    public void UnbalancedWhenRowsWentMissingWithoutARecord()
    {
        // This is the accounting identity that makes "nothing is dropped silently" checkable.
        // A false result is blocking class 1, and the CLI exits non-zero on it.
        var counters = new IngestCounters { RowsRead = 100, RowsInserted = 50 };

        Assert.False(counters.IsBalanced);
        Assert.Equal(50, counters.Accounted);
    }

    [Fact]
    public void AnEmptyRunIsBalanced() =>
        Assert.True(new IngestCounters().IsBalanced);

    [Fact]
    public void DuplicateCountersAreSeparateSoTheIdempotencyProofStaysReadable()
    {
        // 38.2% of a single DMA file is duplicate on FIRST ingest because the feed merges
        // receiving stations. One combined counter would read ~38% on a first run and ~100% on
        // a second, and a reader could not tell which part demonstrates idempotency (ADR-0012).
        var firstRun = new IngestCounters
        {
            RowsRead = 100,
            RowsInserted = 62,
            RowsDuplicateInFile = 38,
        };
        var secondRun = new IngestCounters
        {
            RowsRead = 100,
            RowsInserted = 0,
            RowsDuplicateInFile = 38,
            RowsDuplicatePriorRun = 62,
        };

        Assert.True(firstRun.IsBalanced);
        Assert.True(secondRun.IsBalanced);
        Assert.Equal(0, firstRun.RowsDuplicatePriorRun);
        Assert.Equal(0, secondRun.RowsInserted);
    }
}
