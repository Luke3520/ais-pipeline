using AisPipeline.Core.Laytime;

namespace AisPipeline.Tests.Laytime;

/// <summary>
/// The component that produces the number a demurrage claim is argued over. Every threshold in it
/// has the exact value on each side pinned, and every statement is checked to decompose back to
/// the elapsed time.
/// </summary>
public class LaytimeCalculatorTests
{
    private static readonly DateTime Nor = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);

    private static CharterPartyTerms Terms(
        double allowedHours = 72.0,
        double turnTimeHours = 6.0,
        LaytimeCommencement commencement = LaytimeCommencement.TurnTimeOrBerthingWhicheverFirst,
        bool onceOnDemurrage = true,
        params LaytimeException[] exceptions) => new()
        {
            LaytimeAllowedHours = allowedHours,
            DemurrageRatePerDay = Money.FromMajor(28_000m, "USD"),
            NoticeOfReadinessUtc = Nor,
            TurnTimeHours = turnTimeHours,
            Commencement = commencement,
            OnceOnDemurrageAlwaysOnDemurrage = onceOnDemurrage,
            Exceptions = exceptions,
        };

    private static readonly LaytimeCalculator Calculator = new();

    // ---- commencement ----

    [Fact]
    public void LaytimeStartsAfterTurnTimeWhenTheVesselBerthsLater()
    {
        var statement = Calculator.Calculate(Terms(), berthedUtc: Nor.AddHours(20), Nor.AddHours(30));

        Assert.Equal(Nor.AddHours(6), statement.CommencedUtc);
        Assert.Contains("turn time", statement.CommencementReason, StringComparison.Ordinal);
    }

    [Fact]
    public void BerthingEarlyStartsTheClockEarly()
    {
        // The point of "whichever first": work can begin once alongside, so the charterer does not
        // get the rest of its turn time free.
        var statement = Calculator.Calculate(Terms(), berthedUtc: Nor.AddHours(2), Nor.AddHours(30));

        Assert.Equal(Nor.AddHours(2), statement.CommencedUtc);
        Assert.Contains("berthed", statement.CommencementReason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(5.99, 5.99)]   // berthed just inside turn time: berthing wins
    [InlineData(6.0, 6.0)]     // boundary: equal, so turn time applies
    [InlineData(6.01, 6.0)]    // berthed after: turn time wins
    public void CommencementBoundaryAtTurnTime(double berthedAfterHours, double expectedAfterHours)
    {
        var statement = Calculator.Calculate(
            Terms(), Nor.AddHours(berthedAfterHours), Nor.AddHours(100));

        Assert.Equal(Nor.AddHours(expectedAfterHours), statement.CommencedUtc);
    }

    [Fact]
    public void TurnTimeAfterNoticeIgnoresBerthingEntirely()
    {
        var statement = Calculator.Calculate(
            Terms(commencement: LaytimeCommencement.TurnTimeAfterNotice),
            berthedUtc: Nor.AddHours(1),
            Nor.AddHours(30));

        Assert.Equal(Nor.AddHours(6), statement.CommencedUtc);
    }

    [Fact]
    public void OnBerthingWithoutABerthingTimeIsRefusedRatherThanGuessed() =>
        Assert.Throws<ArgumentNullException>(() => Calculator.Calculate(
            Terms(commencement: LaytimeCommencement.OnBerthing), berthedUtc: null, Nor.AddHours(30)));

    [Fact]
    public void CompletingBeforeCommencementIsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Calculator.Calculate(Terms(), berthedUtc: null, Nor.AddHours(1)));

    // ---- within laytime ----

    [Fact]
    public void FinishingInsideTheAllowanceOwesNothing()
    {
        var statement = Calculator.Calculate(Terms(allowedHours: 72), null, Nor.AddHours(6 + 50));

        Assert.Equal(50.0, statement.UsedHours, 6);
        Assert.Equal(0.0, statement.DemurrageHours, 6);
        Assert.Equal(0, statement.DemurrageOwed.MinorUnits);
        Assert.Equal(22.0, statement.HoursSaved, 6);
    }

    [Theory]
    [InlineData(71.99, 0.0)]
    [InlineData(72.0, 0.0)]     // boundary: exactly the allowance owes nothing
    [InlineData(72.01, 0.01)]   // first hour past it
    public void AllowanceBoundary(double usedHours, double expectedDemurrageHours)
    {
        var statement = Calculator.Calculate(Terms(allowedHours: 72), null, Nor.AddHours(6 + usedHours));

        Assert.Equal(expectedDemurrageHours, statement.DemurrageHours, 6);
    }

    // ---- demurrage ----

    [Fact]
    public void DemurrageIsTheDailyRateProRata()
    {
        // 72 allowed, 96 used, so 24 hours on demurrage = exactly one day at the rate.
        var statement = Calculator.Calculate(Terms(allowedHours: 72), null, Nor.AddHours(6 + 96));

        Assert.Equal(24.0, statement.DemurrageHours, 6);
        Assert.Equal(Money.FromMajor(28_000m, "USD"), statement.DemurrageOwed);
    }

    [Fact]
    public void APartialDayOnDemurrageIsChargedToTheCent()
    {
        // 100 hours over the allowance at 28,000/day = 116,666.666..., which is 116,666.67.
        var statement = Calculator.Calculate(Terms(allowedHours: 72), null, Nor.AddHours(6 + 172));

        Assert.Equal(100.0, statement.DemurrageHours, 6);
        Assert.Equal(116_666.67m, statement.DemurrageOwed.Major);
    }

    [Fact]
    public void TheStatementSplitsTheIntervalAtTheMomentLaytimeRunsOut()
    {
        // A single stretch that starts inside laytime and ends outside it is partly free and
        // partly chargeable. Rounding it to one or the other would be wrong by the remainder.
        var statement = Calculator.Calculate(Terms(allowedHours: 72), null, Nor.AddHours(6 + 96));

        Assert.Contains(statement.Lines, l => l.Kind == LaytimeLineKind.Counted);
        Assert.Contains(statement.Lines, l => l.Kind == LaytimeLineKind.OnDemurrage);
        Assert.Equal(72.0, statement.Lines.Where(l => l.Kind == LaytimeLineKind.Counted).Sum(l => l.Hours), 6);
        Assert.Equal(24.0, statement.Lines.Where(l => l.Kind == LaytimeLineKind.OnDemurrage).Sum(l => l.Hours), 6);
    }

    // ---- exceptions ----

    [Fact]
    public void AnExceptedPeriodDoesNotCountAgainstLaytime()
    {
        var start = Nor.AddHours(6);
        var statement = Calculator.Calculate(
            Terms(allowedHours: 72, exceptions: new LaytimeException(
                start.AddHours(10), start.AddHours(20), "rain stopped loading")),
            null,
            start.AddHours(50));

        Assert.Equal(40.0, statement.UsedHours, 6);
        Assert.Equal(10.0, statement.ExceptedHours, 6);
        Assert.Contains(statement.Lines,
            l => l.Kind == LaytimeLineKind.Excepted && l.Reason.Contains("rain", StringComparison.Ordinal));
    }

    [Fact]
    public void OverlappingExceptionsAreNotDeductedTwice()
    {
        // Rain during a shore breakdown is one interruption. Deducting both would inflate the
        // charterer's case and get the claim rejected.
        var start = Nor.AddHours(6);
        var statement = Calculator.Calculate(
            Terms(allowedHours: 72,
                exceptions: new[]
                {
                    new LaytimeException(start.AddHours(10), start.AddHours(20), "rain"),
                    new LaytimeException(start.AddHours(15), start.AddHours(25), "shore pump failure"),
                }),
            null,
            start.AddHours(50));

        Assert.Equal(15.0, statement.ExceptedHours, 6);
        Assert.Equal(35.0, statement.UsedHours, 6);
    }

    [Fact]
    public void AnExceptionOutsideTheLaytimeWindowIsIgnored()
    {
        var statement = Calculator.Calculate(
            Terms(allowedHours: 72, exceptions: new LaytimeException(
                Nor.AddHours(-10), Nor.AddHours(-5), "before the vessel arrived")),
            null,
            Nor.AddHours(6 + 40));

        Assert.Equal(0.0, statement.ExceptedHours, 6);
        Assert.Equal(40.0, statement.UsedHours, 6);
    }

    [Fact]
    public void AnExceptionIsClippedToTheLaytimeWindow()
    {
        var start = Nor.AddHours(6);
        var statement = Calculator.Calculate(
            Terms(allowedHours: 72, exceptions: new LaytimeException(
                start.AddHours(-5), start.AddHours(5), "weather spanning commencement")),
            null,
            start.AddHours(40));

        Assert.Equal(5.0, statement.ExceptedHours, 6);
    }

    // ---- once on demurrage, always on demurrage ----

    [Fact]
    public void OnceOnDemurrageAnExceptionNoLongerStopsTheClock()
    {
        // The charterer has already used the free time it bargained for, so an interruption it
        // would otherwise have been entitled to no longer counts.
        var start = Nor.AddHours(6);
        var statement = Calculator.Calculate(
            Terms(allowedHours: 20, exceptions: new LaytimeException(
                start.AddHours(30), start.AddHours(40), "rain")),
            null,
            start.AddHours(50));

        Assert.Equal(50.0, statement.UsedHours, 6);
        Assert.Equal(0.0, statement.ExceptedHours, 6);
        Assert.Equal(30.0, statement.DemurrageHours, 6);
        Assert.Contains(statement.Lines,
            l => l.Reason.Contains("already on demurrage", StringComparison.Ordinal));
    }

    [Fact]
    public void ACharterThatContractsOutStillDeductsAfterDemurrageBegins()
    {
        var start = Nor.AddHours(6);
        var statement = Calculator.Calculate(
            Terms(allowedHours: 20, onceOnDemurrage: false, exceptions: new LaytimeException(
                start.AddHours(30), start.AddHours(40), "rain")),
            null,
            start.AddHours(50));

        Assert.Equal(10.0, statement.ExceptedHours, 6);
        Assert.Equal(40.0, statement.UsedHours, 6);
    }

    // ---- the accounting identity ----

    [Theory]
    [InlineData(30.0)]
    [InlineData(72.0)]
    [InlineData(150.0)]
    public void EveryHourBetweenCommencementAndCompletionIsAccountedFor(double elapsedHours)
    {
        var start = Nor.AddHours(6);
        var statement = Calculator.Calculate(
            Terms(allowedHours: 72,
                exceptions: new[]
                {
                    new LaytimeException(start.AddHours(5), start.AddHours(8), "rain"),
                    new LaytimeException(start.AddHours(20), start.AddHours(21), "shifting"),
                }),
            null,
            start.AddHours(elapsedHours));

        Assert.True(statement.IsBalanced,
            $"statement does not decompose: {elapsedHours}h elapsed, lines sum differently");
    }

    [Fact]
    public void TheStatementReadsAsSomethingAHumanWouldCheck()
    {
        var start = Nor.AddHours(6);
        var statement = Calculator.Calculate(
            Terms(allowedHours: 72, exceptions: new LaytimeException(
                start.AddHours(10), start.AddHours(16), "rain stopped loading")),
            berthedUtc: Nor.AddHours(20),
            start.AddHours(96));

        var rendered = statement.ToString();

        Assert.Contains("ON DEMURRAGE", rendered, StringComparison.Ordinal);
        Assert.Contains("rain stopped loading", rendered, StringComparison.Ordinal);
        Assert.Contains("USD", rendered, StringComparison.Ordinal);
    }
}
