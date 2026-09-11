namespace AisPipeline.Core.Laytime;

/// <summary>What a stretch of time did to the laytime account.</summary>
public enum LaytimeLineKind
{
    /// <summary>Before laytime commenced. Not charged, shown so the gap is visible.</summary>
    BeforeCommencement,

    /// <summary>Counted against the allowance.</summary>
    Counted,

    /// <summary>An excepted period. Not counted, with the reason attached.</summary>
    Excepted,

    /// <summary>Counted, and past the allowance, so accruing demurrage.</summary>
    OnDemurrage,
}

/// <summary>
/// One stretch of time and what was done with it.
///
/// The unit of provenance for a laytime calculation: a total nobody can decompose is a total
/// nobody can dispute, and disputing it is the entire point. Every hour in the statement belongs
/// to exactly one line, and every line says why.
/// </summary>
public sealed record LaytimeLine(
    DateTime FromUtc,
    DateTime ToUtc,
    LaytimeLineKind Kind,
    string Reason)
{
    public double Hours => (ToUtc - FromUtc).TotalHours;

    public override string ToString() =>
        $"{FromUtc:yyyy-MM-dd HH:mm} -> {ToUtc:yyyy-MM-dd HH:mm}  {Hours,8:F2}h  {Kind,-18} {Reason}";
}

/// <summary>
/// The result: what was counted, what was allowed, and what is owed.
/// </summary>
public sealed record LaytimeStatement
{
    public required IReadOnlyList<LaytimeLine> Lines { get; init; }

    /// <summary>When laytime began counting, and why.</summary>
    public required DateTime CommencedUtc { get; init; }

    public required string CommencementReason { get; init; }

    /// <summary>When cargo operations completed.</summary>
    public required DateTime CompletedUtc { get; init; }

    public required double AllowedHours { get; init; }

    /// <summary>Hours charged against the allowance, excepted periods removed.</summary>
    public double UsedHours => Lines
        .Where(l => l.Kind is LaytimeLineKind.Counted or LaytimeLineKind.OnDemurrage)
        .Sum(l => l.Hours);

    public double ExceptedHours => Lines
        .Where(l => l.Kind == LaytimeLineKind.Excepted)
        .Sum(l => l.Hours);

    /// <summary>Hours beyond the allowance. Zero when the vessel finished within laytime.</summary>
    public double DemurrageHours => Math.Max(0.0, UsedHours - AllowedHours);

    /// <summary>
    /// Unused allowance. Reported because a charterer wants to see it, but this does NOT become
    /// despatch: tanker charters normally do not provide for it, and inventing a credit the terms
    /// do not grant would be worse than reporting nothing.
    /// </summary>
    public double HoursSaved => Math.Max(0.0, AllowedHours - UsedHours);

    public required Money DemurrageRatePerDay { get; init; }

    /// <summary>
    /// Demurrage owed: the daily rate pro rata over the hours past the allowance.
    ///
    /// One rational multiplication rounded once, rather than a chain of divisions -- see
    /// <see cref="Money.Prorated"/>.
    /// </summary>
    public Money DemurrageOwed => DemurrageRatePerDay.Prorated((decimal)DemurrageHours, 24m);

    /// <summary>
    /// Every hour between commencement and completion belongs to exactly one line.
    ///
    /// The same accounting identity ingest uses (<c>IngestCounters.IsBalanced</c>): a figure you
    /// cannot decompose back to its inputs is a figure nobody can check. A false result means time
    /// went missing from the statement.
    /// </summary>
    public bool IsBalanced
    {
        get
        {
            var span = (CompletedUtc - CommencedUtc).TotalHours;
            var accounted = Lines
                .Where(l => l.Kind != LaytimeLineKind.BeforeCommencement)
                .Sum(l => l.Hours);

            return Math.Abs(span - accounted) < 1e-6;
        }
    }

    public override string ToString()
    {
        var lines = string.Join(Environment.NewLine, Lines.Select(l => $"  {l}"));
        return $"""
            Laytime commenced {CommencedUtc:yyyy-MM-dd HH:mm} ({CommencementReason})
            {lines}
              allowed   {AllowedHours,8:F2}h
              used      {UsedHours,8:F2}h
              excepted  {ExceptedHours,8:F2}h
            {(DemurrageHours > 0
                ? $"  ON DEMURRAGE {DemurrageHours:F2}h at {DemurrageRatePerDay}/day = {DemurrageOwed}"
                : $"  within laytime, {HoursSaved:F2}h saved")}
            """;
    }
}
