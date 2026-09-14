using AisPipeline.Core.Benchmarks;
using AisPipeline.Core.Query;

namespace AisPipeline.Tests.Benchmarks;

/// <summary>
/// "Is 40 hours at Skagen normal?" — and what has to be true before that question can be answered.
/// </summary>
public class PortBenchmarkTests
{
    private static PortCallHours Call(
        double waiting, bool complete = true, double distanceNm = 1.0, string port = "Skagen Havn") =>
        new()
        {
            WpiNumber = 25200,
            PortName = port,
            Country = "Denmark",
            WaitingHours = waiting,
            WorkingHours = 10.0,
            DistanceNm = distanceNm,
            IsComplete = complete,
        };

    private static PortBenchmark Only(IEnumerable<PortCallHours> calls) =>
        Assert.Single(PortBenchmarkBuilder.Build([.. calls]));

    [Fact]
    public void The_median_describes_the_usable_calls_and_the_count_travels_with_it()
    {
        var benchmark = Only(new[] { 2.0, 4.0, 6.0, 8.0, 100.0 }.Select(h => Call(h)));

        Assert.Equal(5, benchmark.UsableCalls);
        Assert.Equal(6.0, benchmark.MedianWaitingHours);
    }

    [Fact]
    public void An_incomplete_call_is_excluded_and_counted_as_excluded()
    {
        // Its hours are a lower bound. Averaging a bound into a median drags it down and nothing
        // in the output would say why (ADR-0011).
        var benchmark = Only([
            Call(10), Call(12), Call(14), Call(16), Call(18),
            Call(1, complete: false), Call(2, complete: false),
        ]);

        Assert.Equal(7, benchmark.AttributedCalls);
        Assert.Equal(2, benchmark.ExcludedIncomplete);
        Assert.Equal(5, benchmark.UsableCalls);
        Assert.Equal(14.0, benchmark.MedianWaitingHours);
    }

    [Fact]
    public void A_call_attributed_from_far_off_is_excluded_and_counted_separately()
    {
        // Nearest to the port is not the same as at it: a 14 nm attribution is an anchorage in
        // open water, and it does not belong in a berth benchmark (ADR-0034).
        var benchmark = Only([
            Call(10), Call(12), Call(14), Call(16), Call(18),
            Call(200, distanceNm: 14.0),
        ]);

        Assert.Equal(1, benchmark.ExcludedTooFar);
        Assert.Equal(5, benchmark.UsableCalls);
        Assert.Equal(14.0, benchmark.MedianWaitingHours);
    }

    [Fact]
    public void The_two_exclusion_reasons_do_not_double_count_one_call()
    {
        var benchmark = Only([Call(5, complete: false, distanceNm: 20.0)]);

        Assert.Equal(1, benchmark.AttributedCalls);
        Assert.Equal(1, benchmark.ExcludedIncomplete);
        Assert.Equal(0, benchmark.ExcludedTooFar);
        Assert.Equal(0, benchmark.UsableCalls);

        // Every attributed call is accounted for by exactly one outcome.
        Assert.Equal(
            benchmark.AttributedCalls,
            benchmark.ExcludedIncomplete + benchmark.ExcludedTooFar + benchmark.UsableCalls);
    }

    [Fact]
    public void Too_few_calls_yields_no_median_rather_than_a_thin_one()
    {
        // Null, not a number the caller must remember to qualify -- the same convention the read
        // models use for a censored duration.
        var benchmark = Only(new[] { 4.0, 6.0, 8.0, 10.0 }.Select(h => Call(h)));

        Assert.Equal(4, benchmark.UsableCalls);
        Assert.Null(benchmark.MedianWaitingHours);
        Assert.Null(benchmark.RankWaiting(40));
    }

    [Fact]
    public void A_p90_needs_more_evidence_than_a_median()
    {
        // From nine observations a p90 is simply the largest of nine: a maximum wearing a
        // percentile's name.
        var nine = Only(Enumerable.Range(1, 9).Select(i => Call(i)));
        Assert.NotNull(nine.MedianWaitingHours);
        Assert.Null(nine.P90WaitingHours);

        var ten = Only(Enumerable.Range(1, 10).Select(i => Call(i)));
        Assert.NotNull(ten.P90WaitingHours);
    }

    [Fact]
    public void Percentiles_are_values_that_actually_happened()
    {
        // Nearest-rank, not interpolated. With samples this small, interpolation invents a duration
        // no vessel experienced, and the figure exists to describe calls that did happen.
        var benchmark = Only(new[] { 1.0, 2.0, 3.0, 4.0, 100.0 }.Select(h => Call(h)));

        Assert.Contains(benchmark.MedianWaitingHours!.Value, benchmark.WaitingHours);
    }

    [Fact]
    public void A_wait_is_ranked_against_what_the_port_actually_did()
    {
        // The real question. 40 hours against Skagen's record is not "bad", it is a position.
        var benchmark = Only(new[] { 2.0, 5.0, 9.0, 12.0, 20.0, 25.0, 28.0, 42.0, 60.0, 77.0 }
            .Select(h => Call(h)));

        Assert.Equal(70.0, benchmark.RankWaiting(40)!.Value, 1);
        Assert.Equal(100.0, benchmark.RankWaiting(1000)!.Value, 1);
        Assert.Equal(0.0, benchmark.RankWaiting(0.5)!.Value, 1);
    }

    [Fact]
    public void Ports_are_separated_and_ordered_by_how_much_evidence_they_have()
    {
        var benchmarks = PortBenchmarkBuilder.Build([
            Call(5, port: "Small"),
            .. Enumerable.Range(1, 6).Select(i => Call(i, port: "Busy")),
        ]);

        Assert.Equal(["Busy", "Small"], benchmarks.Select(b => b.PortName).ToList());
        Assert.Equal(6, benchmarks[0].UsableCalls);
    }
}
