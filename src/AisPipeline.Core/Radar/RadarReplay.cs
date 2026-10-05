using System.Globalization;
using AisPipeline.Core.Domain;
using AisPipeline.Core.Query;

namespace AisPipeline.Core.Radar;

public enum ScopeState
{
    Moving,
    Stopped,

    /// <summary>Inside a detected stop whose status disagrees with its speed (R10).</summary>
    StoppedClaimingUnderWay,

    /// <summary>The latest fix carries R12: moving while claiming to be stationary.</summary>
    MovingClaimingStationary,
}

/// <summary>A vessel as the scope shows it at one instant.</summary>
public sealed record VesselOnScope(
    long Mmsi,
    string Name,
    double LatitudeDeg,
    double LongitudeDeg,
    double? CourseDeg,
    ScopeState State,
    IReadOnlyList<(double LatDeg, double LonDeg)> Trail,
    DateTime LastFixUtc);

/// <summary>
/// Plays the stored track back in time order and says, at any instant, what is on the scope and
/// what has just happened.
///
/// It is fed one fix at a time and keeps only a trail's worth per vessel, so a week of five
/// million fixes replays in constant memory while the database streams it.
///
/// **It decides nothing.** Whether a vessel is stopped is read from the stops detection already
/// wrote, and whether its status contradicts it is read from R10 on those stops and R12 on the
/// fix. Recomputing either here would give the radar its own opinion about the data, and a second
/// opinion that can disagree with the first is exactly what rule 4 forbids the pipeline to invent.
/// </summary>
public sealed class RadarReplay
{
    private readonly IReadOnlyDictionary<long, string> _names;
    private readonly Dictionary<long, List<StoredStop>> _stopsByVessel;
    private readonly Dictionary<long, List<StoredPortCall>> _callsByVessel;
    private readonly Dictionary<long, Track> _tracks = [];
    private DateTime _clockUtc = DateTime.MinValue;

    public RadarReplay(
        IEnumerable<StoredStop> stops,
        IEnumerable<StoredPortCall> portCalls,
        IReadOnlyDictionary<long, string> names)
    {
        _names = names;
        _stopsByVessel = stops.GroupBy(s => s.Mmsi)
            .ToDictionary(g => g.Key, g => g.OrderBy(s => s.StartedUtc).ToList());
        _callsByVessel = portCalls.GroupBy(c => c.Mmsi)
            .ToDictionary(g => g.Key, g => g.ToList());
    }

    /// <summary>
    /// Take the next fix. Fixes must arrive in timestamp order across all vessels; anything else
    /// is a caller bug and throws rather than drawing a vessel moving backwards in time.
    /// </summary>
    public IReadOnlyList<LogEntry> Advance(TrackFix fix)
    {
        if (fix.TimestampUtc < _clockUtc)
        {
            throw new ArgumentException(
                $"fix {fix.Id} at {fix.TimestampUtc:O} arrived after {_clockUtc:O}; replay needs time order",
                nameof(fix));
        }

        _clockUtc = fix.TimestampUtc;

        if (!_tracks.TryGetValue(fix.Mmsi, out var track))
        {
            track = new Track();
            _tracks[fix.Mmsi] = track;
        }

        var entries = new List<LogEntry>(1);
        var name = NameOf(fix.Mmsi);

        // The stop that just ended reports on the first fix after it, so the line can say the
        // stop is over rather than guessing from its last stationary fix that it is about to be.
        if (track.EndingStop is { } ended && fix.Id != ended.LastPositionId)
        {
            entries.Add(Entry(fix, LogKind.StopEnded,
                $"{name} ends a {Hours(ended)} stop{AtPort(ended)}."));
            track.EndingStop = null;
        }

        var stop = StopContaining(fix);
        if (stop is not null && fix.Id == stop.FirstPositionId)
        {
            entries.Add(stop.StatusAgrees
                ? Entry(fix, LogKind.Stopped, $"{name} stopped{AtPort(stop)}.")
                : Entry(fix, LogKind.StoppedClaimingUnderWay,
                    $"{name} stopped{AtPort(stop)}. Transponder says: {Shout(stop.ReportedStatus)}."));
        }

        if (stop is not null && fix.Id == stop.LastPositionId)
        {
            track.EndingStop = stop;
        }

        var r12 = fix.IsFlagged(RuleIds.StatusClaimsStationary);
        if (r12 && !track.LatestFlaggedR12)
        {
            entries.Add(Entry(fix, LogKind.MovingClaimingStationary, string.Create(
                CultureInfo.InvariantCulture,
                $"{name} making {fix.SpeedOverGroundKn:0.0} kn. Transponder says: {Shout(fix.NavigationalStatus)}.")));
        }

        track.LatestFlaggedR12 = r12;
        track.Stop = stop;
        track.Latest = fix;
        track.Push(fix);

        return entries;
    }

    /// <summary>Every vessel heard from within <see cref="RadarThresholds.StaleAfter"/> of now.</summary>
    public IReadOnlyList<VesselOnScope> Scope()
    {
        var cutoff = _clockUtc - RadarThresholds.StaleAfter;
        var scope = new List<VesselOnScope>(_tracks.Count);

        foreach (var (mmsi, track) in _tracks)
        {
            if (track.Latest is not { } latest || latest.TimestampUtc < cutoff)
            {
                continue;
            }

            // Draw only through fixes whose position a rule has not discredited (R7, R11). One
            // teleported fix otherwise drags a line across the chart that the vessel never sailed,
            // and the scenery would be asserting what the stop detector refused to (ADR-0021).
            var reliable = track.Trail.Where(f => !IsPositionUnreliable(f)).ToList();
            if (reliable.Count == 0)
            {
                continue;
            }

            var shown = reliable[^1];
            var state = track.Stop switch
            {
                { StatusAgrees: false } => ScopeState.StoppedClaimingUnderWay,
                not null => ScopeState.Stopped,
                null when track.LatestFlaggedR12 => ScopeState.MovingClaimingStationary,
                null => ScopeState.Moving,
            };

            scope.Add(new VesselOnScope(
                mmsi,
                NameOf(mmsi),
                shown.Latitude,
                shown.Longitude,
                shown.CourseOverGroundDeg,
                state,
                [.. reliable.Select(f => (f.Latitude, f.Longitude))],
                latest.TimestampUtc));
        }

        return [.. scope.OrderBy(v => v.Mmsi)];
    }

    public DateTime ClockUtc => _clockUtc;

    private StoredStop? StopContaining(TrackFix fix)
    {
        if (!_stopsByVessel.TryGetValue(fix.Mmsi, out var stops))
        {
            return null;
        }

        foreach (var s in stops)
        {
            if (s.StartedUtc > fix.TimestampUtc)
            {
                return null;
            }

            if (fix.TimestampUtc <= s.EndedUtc)
            {
                return s;
            }
        }

        return null;
    }

    /// <summary>
    /// " at Fredericia" when the stop sits in a port call plausibly at a named port, otherwise
    /// nothing. A port is named only where ADR-0034's distance says the vessel was plausibly
    /// there; nearest-to is not at.
    /// </summary>
    private string AtPort(StoredStop stop)
    {
        if (!_callsByVessel.TryGetValue(stop.Mmsi, out var calls))
        {
            return "";
        }

        var call = calls.FirstOrDefault(c => c.ArrivedUtc <= stop.StartedUtc && stop.StartedUtc <= c.DepartedUtc);
        return call is { PlausiblyAtPort: true, PortName: { } port } ? $" at {port}" : "";
    }

    /// <summary>
    /// "14.2 h", or "≥14.2 h" when detection could not see the stop's whole extent. The same
    /// convention the rest of the pipeline prints: a lower bound is never shown as a measurement.
    /// </summary>
    private static string Hours(StoredStop stop) => stop.DurationHours is { } h
        ? string.Create(CultureInfo.InvariantCulture, $"{h:0.0} h")
        : string.Create(CultureInfo.InvariantCulture, $"≥{stop.ObservedDurationHours:0.0} h");

    private static string Shout(string? status) =>
        string.IsNullOrWhiteSpace(status) ? "NOTHING" : status.ToUpperInvariant();

    private static bool IsPositionUnreliable(TrackFix f) =>
        RuleIds.PositionUnreliable.Any(f.IsFlagged);

    private string NameOf(long mmsi) =>
        _names.TryGetValue(mmsi, out var n) && !string.IsNullOrWhiteSpace(n)
            ? n.Trim()
            : string.Create(CultureInfo.InvariantCulture, $"MMSI {mmsi}");

    private static LogEntry Entry(TrackFix fix, LogKind kind, string text) =>
        new(fix.TimestampUtc, fix.Mmsi, kind, text, fix.Citation);

    private sealed class Track
    {
        private readonly Queue<TrackFix> _trail = new();

        public TrackFix? Latest { get; set; }
        public StoredStop? Stop { get; set; }
        public StoredStop? EndingStop { get; set; }
        public bool LatestFlaggedR12 { get; set; }
        public IEnumerable<TrackFix> Trail => _trail;

        public void Push(TrackFix fix)
        {
            _trail.Enqueue(fix);
            var cutoff = fix.TimestampUtc - RadarThresholds.TrailDuration;
            while (_trail.Peek().TimestampUtc < cutoff)
            {
                _trail.Dequeue();
            }
        }
    }
}
