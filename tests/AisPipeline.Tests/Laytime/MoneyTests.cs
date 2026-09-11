using AisPipeline.Core.Laytime;

namespace AisPipeline.Tests.Laytime;

public class MoneyTests
{
    [Fact]
    public void MajorUnitsRoundTripExactly()
    {
        // The canary: 0.1 + 0.2 is not 0.3 in binary floating point. Held in minor units it is.
        var total = Money.FromMajor(0.1m, "USD") + Money.FromMajor(0.2m, "USD");

        Assert.Equal(30, total.MinorUnits);
        Assert.Equal(0.30m, total.Major);
    }

    [Fact]
    public void ARateIsHeldToTheCent()
    {
        Assert.Equal(2_800_000, Money.FromMajor(28_000m, "USD").MinorUnits);
    }

    [Theory]
    [InlineData(24, 2_800_000)]      // a full day at the daily rate
    [InlineData(12, 1_400_000)]      // half a day
    [InlineData(0, 0)]
    [InlineData(48, 5_600_000)]
    public void ProratingIsExactOnWholeFractions(double hours, long expectedMinorUnits)
    {
        var owed = Money.FromMajor(28_000m, "USD").Prorated((decimal)hours, 24m);

        Assert.Equal(expectedMinorUnits, owed.MinorUnits);
    }

    [Fact]
    public void ProratingRoundsHalfAwayFromZeroNotToEven()
    {
        // Commercial convention, not banker's rounding. A counterparty checking the arithmetic by
        // hand will round half up, and a statement that disagreed by a cent invites an argument
        // about the method rather than the facts.
        var owed = Money.FromMajor(1m, "USD").Prorated(1m, 8m);   // 12.5 cents

        Assert.Equal(13, owed.MinorUnits);
    }

    [Fact]
    public void ProratingIsOneMultiplicationNotAChainOfDivisions()
    {
        // 100 hours at 28,000/day is 116,666.666... The exact answer to the cent is 116,666.67.
        // Dividing the rate by 24 first and then multiplying loses precision before the multiply.
        var owed = Money.FromMajor(28_000m, "USD").Prorated(100m, 24m);

        Assert.Equal(11_666_667, owed.MinorUnits);
        Assert.Equal(116_666.67m, owed.Major);
    }

    [Fact]
    public void CombiningDifferentCurrenciesIsRefused()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Money.FromMajor(1m, "USD") + Money.FromMajor(1m, "EUR"));
    }

    [Fact]
    public void ProratingOverZeroIsRefusedRatherThanInfinite() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Money.FromMajor(1m, "USD").Prorated(1m, 0m));
}
