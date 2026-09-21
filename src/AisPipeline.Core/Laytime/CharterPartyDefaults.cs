namespace AisPipeline.Core.Laytime;

/// <summary>
/// The terms used when a caller supplies none.
///
/// These are **illustrative tanker defaults, not a charter party**. The engine is correct for the
/// terms it is given and the terms are an input; these exist so a reader can get a figure out of
/// the pipeline without first negotiating a fixture, and every surface that applies them says so.
///
/// Named here because they were previously inline literals in two places -- the API's laytime
/// lambda and the CLI's <c>--allowed/--rate/--turn</c> argument defaults -- which is one copy more
/// than can be kept in step. A demurrage figure that differs between the CLI and the browser for
/// no reason but a mistyped default is the kind of disagreement this project exists to prevent.
/// </summary>
public static class CharterPartyDefaults
{
    /// <summary>Laytime allowed, in hours. Seventy-two: three days, a common tanker term.</summary>
    public const double AllowedHours = 72.0;

    /// <summary>Demurrage rate per day, in <see cref="Currency"/>.</summary>
    public const double RatePerDay = 28_000.0;

    /// <summary>Turn time, in hours -- the grace between notice and laytime starting to count.</summary>
    public const double TurnHours = 6.0;

    public const string Currency = "USD";
}
