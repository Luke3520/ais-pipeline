namespace AisPipeline.Core.Laytime;

/// <summary>
/// Turns charter party terms and a voyage timeline into a laytime statement.
///
/// Pure interval arithmetic: no AIS, no database, no clock. The inputs are terms and two
/// timestamps, and the output decomposes into lines that sum back to the elapsed time. That makes
/// it the most testable component in the project, and it has to be -- this produces the number a
/// demurrage claim is argued over.
/// </summary>
public sealed class LaytimeCalculator
{
    /// <summary>
    /// Calculate laytime between commencement and completion of cargo operations.
    /// </summary>
    /// <param name="terms">The charter party terms.</param>
    /// <param name="berthedUtc">
    /// When the vessel berthed. Required for the commencement rules that depend on it; may be null
    /// when commencement is turn time after notice.
    /// </param>
    /// <param name="completedUtc">Completion of cargo operations -- the end of laytime.</param>
    public LaytimeStatement Calculate(
        CharterPartyTerms terms, DateTime? berthedUtc, DateTime completedUtc)
    {
        var (commencedUtc, reason) = Commencement(terms, berthedUtc);

        if (completedUtc < commencedUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(completedUtc),
                $"cargo operations completed {completedUtc:O} before laytime commenced {commencedUtc:O}");
        }

        var lines = new List<LaytimeLine>();

        if (commencedUtc > terms.NoticeOfReadinessUtc)
        {
            // Shown rather than omitted: a charterer reading the statement wants to see the gap
            // between tendering and the clock starting, not have it silently absent.
            lines.Add(new LaytimeLine(
                terms.NoticeOfReadinessUtc, commencedUtc,
                LaytimeLineKind.BeforeCommencement,
                terms.NoticeOfReadinessIsAssumed
                    ? "notice ASSUMED at arrival (AIS cannot observe a notice); laytime not yet running"
                    : "notice tendered; laytime not yet running"));
        }

        var exceptions = Merge(terms.Exceptions, commencedUtc, completedUtc);
        var counted = 0.0;
        var cursor = commencedUtc;

        foreach (var exception in exceptions)
        {
            if (exception.FromUtc > cursor)
            {
                counted += Emit(lines, cursor, exception.FromUtc, counted, terms, "cargo operations");
            }

            // "Once on demurrage, always on demurrage." The charterer has already used the free
            // time it bargained for, so an interruption it would otherwise have been entitled to
            // no longer stops the clock. Some charters contract out of it, which is why this is a
            // term rather than an assumption.
            var onDemurrage = terms.OnceOnDemurrageAlwaysOnDemurrage && counted >= terms.LaytimeAllowedHours;

            if (onDemurrage)
            {
                counted += Emit(lines, exception.FromUtc, exception.ToUtc, counted, terms,
                    $"{exception.Reason} (not deducted: already on demurrage)");
            }
            else
            {
                lines.Add(new LaytimeLine(
                    exception.FromUtc, exception.ToUtc, LaytimeLineKind.Excepted, exception.Reason));
            }

            cursor = exception.ToUtc;
        }

        if (cursor < completedUtc)
        {
            Emit(lines, cursor, completedUtc, counted, terms, "cargo operations");
        }

        return new LaytimeStatement
        {
            Lines = lines,
            CommencedUtc = commencedUtc,
            CommencementReason = terms.NoticeOfReadinessIsAssumed
                ? $"{reason}, from an ASSUMED notice time"
                : reason,
            CompletedUtc = completedUtc,
            AllowedHours = terms.LaytimeAllowedHours,
            DemurrageRatePerDay = terms.DemurrageRatePerDay,
        };
    }

    /// <summary>
    /// Emits one counted stretch, splitting it at the moment the allowance runs out.
    ///
    /// The split matters: a single interval that starts inside laytime and ends outside it is
    /// partly free and partly chargeable, and a statement that rounded it to one or the other
    /// would be wrong by however much of it fell on the other side.
    /// </summary>
    private static double Emit(
        List<LaytimeLine> lines,
        DateTime from,
        DateTime to,
        double countedSoFar,
        CharterPartyTerms terms,
        string reason)
    {
        var hours = (to - from).TotalHours;
        var remainingAllowance = terms.LaytimeAllowedHours - countedSoFar;

        if (remainingAllowance <= 0)
        {
            lines.Add(new LaytimeLine(from, to, LaytimeLineKind.OnDemurrage, reason));
            return hours;
        }

        if (hours <= remainingAllowance)
        {
            lines.Add(new LaytimeLine(from, to, LaytimeLineKind.Counted, reason));
            return hours;
        }

        var boundary = from.AddHours(remainingAllowance);
        lines.Add(new LaytimeLine(from, boundary, LaytimeLineKind.Counted, reason));
        lines.Add(new LaytimeLine(boundary, to, LaytimeLineKind.OnDemurrage, reason));
        return hours;
    }

    private static (DateTime Utc, string Reason) Commencement(
        CharterPartyTerms terms, DateTime? berthedUtc)
    {
        var afterTurnTime = terms.NoticeOfReadinessUtc.AddHours(terms.TurnTimeHours);

        switch (terms.Commencement)
        {
            case LaytimeCommencement.TurnTimeAfterNotice:
                return (afterTurnTime, $"{terms.TurnTimeHours:F0}h turn time after notice");

            case LaytimeCommencement.OnBerthing:
                return berthedUtc is { } berthed
                    ? (berthed, "on berthing")
                    : throw new ArgumentNullException(
                        nameof(berthedUtc), "commencement is on berthing, but no berthing time was given");

            case LaytimeCommencement.TurnTimeOrBerthingWhicheverFirst:
                if (berthedUtc is { } b && b < afterTurnTime)
                {
                    return (b, "berthed before turn time expired");
                }

                return (afterTurnTime, $"{terms.TurnTimeHours:F0}h turn time after notice");

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(terms), terms.Commencement, "unsupported commencement rule");
        }
    }

    /// <summary>
    /// Clips exceptions to the laytime window, merges overlaps, and orders them.
    ///
    /// Overlapping exceptions must not deduct twice -- rain during a shore breakdown is one
    /// interruption, not two -- and a claim that double-counted them would be rejected.
    /// </summary>
    private static List<LaytimeException> Merge(
        IReadOnlyList<LaytimeException> exceptions, DateTime from, DateTime to)
    {
        var clipped = exceptions
            .Select(e => e with
            {
                FromUtc = e.FromUtc < from ? from : e.FromUtc,
                ToUtc = e.ToUtc > to ? to : e.ToUtc,
            })
            .Where(e => e.ToUtc > e.FromUtc)
            .OrderBy(e => e.FromUtc)
            .ToList();

        var merged = new List<LaytimeException>();
        foreach (var exception in clipped)
        {
            if (merged.Count > 0 && exception.FromUtc <= merged[^1].ToUtc)
            {
                var previous = merged[^1];
                merged[^1] = previous with
                {
                    ToUtc = exception.ToUtc > previous.ToUtc ? exception.ToUtc : previous.ToUtc,
                    Reason = string.Equals(previous.Reason, exception.Reason, StringComparison.Ordinal)
                        ? previous.Reason
                        : $"{previous.Reason}; {exception.Reason}",
                };
                continue;
            }

            merged.Add(exception);
        }

        return merged;
    }
}
