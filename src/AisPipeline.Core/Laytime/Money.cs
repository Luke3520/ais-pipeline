using System.Globalization;

namespace AisPipeline.Core.Laytime;

/// <summary>
/// An amount of money, held in minor units.
///
/// Never a double. A demurrage rate of $28,000 per day accrued over a few hundred hours and
/// rounded at each step drifts by amounts a counterparty will notice, and floating point cannot
/// represent most decimal amounts exactly -- 0.1 + 0.2 is famously not 0.3. This is a settlement
/// figure in a commercial dispute, so it is integer cents throughout and rounds exactly once, at
/// the point a number is produced.
/// </summary>
public readonly record struct Money(long MinorUnits, string Currency)
{
    public static Money Zero(string currency) => new(0, currency);

    /// <summary>Parse a major-unit amount, e.g. 28000.50 dollars, into minor units.</summary>
    public static Money FromMajor(decimal amount, string currency) =>
        new((long)Math.Round(amount * 100m, MidpointRounding.AwayFromZero), currency);

    public decimal Major => MinorUnits / 100m;

    /// <summary>
    /// A share of this amount: <paramref name="numerator"/>/<paramref name="denominator"/> of it.
    ///
    /// Demurrage accrues pro rata -- a daily rate charged for part of a day -- so the whole
    /// calculation is one rational multiplication rather than a chain of divisions. Doing it in
    /// decimal and rounding once keeps the result exact to the cent; dividing first would lose
    /// precision before the multiply.
    ///
    /// Rounds half away from zero, the commercial convention, not banker's rounding.
    /// </summary>
    public Money Prorated(decimal numerator, decimal denominator)
    {
        if (denominator == 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(denominator), "cannot prorate over zero");
        }

        var scaled = MinorUnits * numerator / denominator;
        return this with { MinorUnits = (long)Math.Round(scaled, MidpointRounding.AwayFromZero) };
    }

    public static Money operator +(Money left, Money right)
    {
        Require(left, right);
        return left with { MinorUnits = left.MinorUnits + right.MinorUnits };
    }

    public static Money operator -(Money left, Money right)
    {
        Require(left, right);
        return left with { MinorUnits = left.MinorUnits - right.MinorUnits };
    }

    private static void Require(Money left, Money right)
    {
        if (!string.Equals(left.Currency, right.Currency, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"cannot combine {left.Currency} with {right.Currency}");
        }
    }

    public override string ToString() =>
        $"{Major.ToString("N2", CultureInfo.InvariantCulture)} {Currency}";
}
