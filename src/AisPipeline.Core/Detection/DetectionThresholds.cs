namespace AisPipeline.Core.Detection;

/// <summary>
/// Every threshold detection turns on, named, with its unit in the name and its origin here.
///
/// These are heuristics. Several were calibrated against real data and all of them will be
/// retuned against a longer window; an inline literal would be a threshold nobody can find
/// (docs/rules/units-and-geodesy.md).
/// </summary>
public sealed record DetectionThresholds
{
    /// <summary>Below this speed the vessel is considered stopped.</summary>
    public double EnterStoppedKn { get; init; } = 0.5;

    /// <summary>
    /// At or above this speed the vessel is moving again. The gap from
    /// <see cref="EnterStoppedKn"/> is hysteresis: a vessel bobbing around half a knot must
    /// produce one stop rather than fifty.
    /// </summary>
    public double LeaveStoppedKn { get; init; } = 1.0;

    /// <summary>A stopped period shorter than this is not worth recording.</summary>
    public TimeSpan MinimumStopDuration { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Silence longer than this closes the current stop and marks it incomplete. A vessel that
    /// leaves receiver range simply stops appearing; without this it is recorded as stationary
    /// for days it may have spent steaming (ADR-0011).
    ///
    /// Measured on one day of tanker data: 39 consecutive pairs exceed 60 minutes. The rule
    /// matters far more across a multi-day window, where leaving coverage is common.
    /// </summary>
    public TimeSpan MaximumGap { get; init; } = TimeSpan.FromMinutes(60);

    /// <summary>
    /// Berth when <c>max_drift_nm / sqrt(duration_hours)</c> falls below this.
    ///
    /// Normalised by duration rather than compared raw, because a moored vessel's MEASURED
    /// drift grows with how long it sits there: 0.0013 nm under two hours against 0.0346 nm
    /// past seventy-two. A moored ship does not wander down the quay -- GPS error is a random
    /// walk whose maximum excursion accumulates as the square root of elapsed time, and
    /// max_drift is a maximum over fixes, so a longer stop simply gets more draws.
    ///
    /// A fixed threshold cannot track that. It peaks at 79.2% on seven days and makes one
    /// vessel at one quay alternate Berth/Anchorage eleven times in a single port call.
    /// Normalised scores 84.1%, and 81.5% on days it was not fitted to (ADR-0024, superseding
    /// ADR-0020).
    /// </summary>
    public double BerthDriftPerSqrtHour { get; init; } = 0.008;

    /// <summary>Consecutive stops further apart than this belong to different port calls.</summary>
    public double PortCallRadiusNm { get; init; } = 10.0;

    /// <summary>A vessel away longer than this between stops has left, not shifted berth.</summary>
    public TimeSpan PortCallMaximumGap { get; init; } = TimeSpan.FromHours(12);

    public static DetectionThresholds Default { get; } = new();
}
