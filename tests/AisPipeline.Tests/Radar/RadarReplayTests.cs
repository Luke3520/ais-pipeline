using AisPipeline.Core.Domain;
using AisPipeline.Core.Query;
using AisPipeline.Core.Radar;

namespace AisPipeline.Tests.Radar;

/// <summary>
/// The replay reads judgments the pipeline already made and never makes its own. These tests pin
/// both halves: what it says, and that every line it says can be traced to the fix it rests on.
/// </summary>
public class RadarReplayTests
{
    private const long Mmsi = 219000001;
    private static readonly DateTime T0 = new(2026, 9, 4, 22, 0, 0, DateTimeKind.Utc);

    private static TrackFix Fix(long id, int minutes, double sog = 0.1, string? status = "Under way using engine",
        string flags = "", long mmsi = Mmsi, double lat = 56.0, double lon = 11.0) => new()
        {
            Id = id,
            Mmsi = mmsi,
            TimestampUtc = T0.AddMinutes(minutes),
            Latitude = lat,
            Longitude = lon,
            SpeedOverGroundKn = sog,
            NavigationalStatus = status,
            QualityFlags = flags,
            IngestRunId = 3,
            SourceLine = 48_000 + id,
        };

    private static StoredStop Stop(long firstId, int fromMinutes, long lastId, int toMinutes,
        bool agrees = true, bool complete = true, string status = "Under way using engine") => new()
        {
            Id = 1,
            Mmsi = Mmsi,
            StartedUtc = T0.AddMinutes(fromMinutes),
            EndedUtc = T0.AddMinutes(toMinutes),
            ObservedDurationHours = (toMinutes - fromMinutes) / 60.0,
            FirstPositionId = firstId,
            LastPositionId = lastId,
            StatusAgrees = agrees,
            IsComplete = complete,
            ReportedStatus = status,
        };

    private static RadarReplay Replay(params StoredStop[] stops) =>
        new(stops, [], mmsi => mmsi == Mmsi ? "STINGRAY " : null);

    private static List<LogEntry> Play(RadarReplay replay, params TrackFix[] fixes) =>
        [.. fixes.SelectMany(replay.Advance)];

    [Fact]
    public void A_stop_whose_status_disagrees_is_logged_with_what_the_transponder_claimed()
    {
        var replay = Replay(Stop(firstId: 2, 10, lastId: 3, 130, agrees: false));

        var log = Play(replay, Fix(1, 0, sog: 9.0), Fix(2, 10), Fix(3, 130));

        var entry = Assert.Single(log);
        Assert.Equal(LogKind.StoppedClaimingUnderWay, entry.Kind);
        Assert.Equal("STINGRAY stopped. Transponder says: UNDER WAY USING ENGINE.", entry.Text);
    }

    [Fact]
    public void Every_line_cites_the_fix_it_was_emitted_on()
    {
        var replay = Replay(Stop(firstId: 2, 10, lastId: 3, 130));

        var log = Play(replay, Fix(1, 0, sog: 9.0), Fix(2, 10), Fix(3, 130), Fix(4, 140, sog: 8.0));

        Assert.Equal(2, log.Count);
        Assert.Equal(new Citation(3, 48_002), log[0].Citation);
        Assert.Equal(new Citation(3, 48_004), log[1].Citation);
        Assert.All(log, e => Assert.EndsWith(e.Citation.ToString(), e.Render(withDate: false), StringComparison.Ordinal));
    }

    [Fact]
    public void A_stop_end_is_reported_on_the_first_fix_after_it_with_the_stored_duration()
    {
        var replay = Replay(Stop(firstId: 2, 10, lastId: 3, 130));

        var log = Play(replay, Fix(2, 10), Fix(3, 130), Fix(4, 140, sog: 8.0));

        Assert.Equal("STINGRAY ends a 2.0 h stop.", log[^1].Text);
        Assert.Equal(LogKind.StopEnded, log[^1].Kind);
        Assert.Equal(T0.AddMinutes(140), log[^1].AtUtc);
    }

    [Fact]
    public void An_incomplete_stop_prints_its_duration_as_a_lower_bound()
    {
        var replay = Replay(Stop(firstId: 2, 10, lastId: 3, 130, complete: false));

        var log = Play(replay, Fix(2, 10), Fix(3, 130), Fix(4, 400, sog: 8.0));

        Assert.Equal("STINGRAY ends a ≥2.0 h stop.", log[^1].Text);
    }

    [Fact]
    public void A_stop_already_under_way_when_the_replay_starts_is_not_announced()
    {
        // The window opened mid-stop: the fix that began it is not in the stream, so there is no
        // fix to cite for "stopped", and the line is not invented.
        var replay = Replay(Stop(firstId: 1, 0, lastId: 3, 130));

        var log = Play(replay, Fix(2, 60), Fix(3, 130));

        Assert.Empty(log);
        Assert.Equal(ScopeState.Stopped, Assert.Single(replay.Scope()).State);
    }

    [Fact]
    public void R12_is_logged_once_per_episode_not_once_per_fix()
    {
        var replay = Replay();

        var log = Play(replay,
            Fix(1, 0, sog: 11.0, status: "Moored", flags: "R12"),
            Fix(2, 1, sog: 11.2, status: "Moored", flags: "R12"),
            Fix(3, 2, sog: 11.4, status: "Moored"),
            Fix(4, 3, sog: 11.6, status: "Moored", flags: "R12"));

        Assert.Equal(2, log.Count);
        Assert.Equal("STINGRAY making 11.0 kn. Transponder says: MOORED.", log[0].Text);
        Assert.Equal(new Citation(3, 48_004), log[1].Citation);
    }

    [Fact]
    public void Conflict_on_the_scope_comes_from_stored_judgments_not_from_the_radar()
    {
        // Speed 0.1 kn and "Under way using engine" looks exactly like R10, but no stop was
        // detected here. The radar does not second-guess detection: it is drawn as moving.
        var replay = Replay();

        Play(replay, Fix(1, 0, sog: 0.1));

        Assert.Equal(ScopeState.Moving, Assert.Single(replay.Scope()).State);
    }

    [Fact]
    public void Inside_an_R10_stop_the_vessel_is_drawn_as_a_conflict()
    {
        var replay = Replay(Stop(firstId: 1, 0, lastId: 3, 130, agrees: false));

        Play(replay, Fix(1, 0), Fix(2, 10));

        Assert.Equal(ScopeState.StoppedClaimingUnderWay, Assert.Single(replay.Scope()).State);
    }

    [Fact]
    public void A_vessel_silent_past_the_stale_limit_leaves_the_scope()
    {
        var replay = Replay();

        Play(replay,
            Fix(1, 0, sog: 9.0),
            Fix(2, (int)RadarThresholds.StaleAfter.TotalMinutes + 1, sog: 9.0, mmsi: 219000002));

        Assert.Equal([219000002L], replay.Scope().Select(v => v.Mmsi));
    }

    [Fact]
    public void A_teleported_fix_is_neither_drawn_nor_followed_by_the_trail()
    {
        var replay = Replay();

        Play(replay,
            Fix(1, 0, sog: 9.0, lat: 56.00),
            Fix(2, 1, sog: 9.0, lat: 58.50, flags: RuleIds.Teleport),
            Fix(3, 2, sog: 9.0, lat: 56.01));

        var v = Assert.Single(replay.Scope());
        Assert.Equal(56.01, v.LatitudeDeg);
        Assert.DoesNotContain(v.Trail, p => p.LatDeg == 58.50);
    }

    [Fact]
    public void The_trail_keeps_only_the_configured_duration()
    {
        var replay = Replay();
        var minutes = (int)RadarThresholds.TrailDuration.TotalMinutes;

        Play(replay, Fix(1, 0, sog: 9.0), Fix(2, minutes / 2, sog: 9.0), Fix(3, minutes + 1, sog: 9.0));

        Assert.Equal(2, Assert.Single(replay.Scope()).Trail.Count);
    }

    [Fact]
    public void Fixes_out_of_time_order_are_refused()
    {
        var replay = Replay();
        replay.Advance(Fix(1, 10));

        Assert.Throws<ArgumentException>(() => replay.Advance(Fix(2, 5)));
    }

    [Fact]
    public void An_unnamed_vessel_is_called_by_its_mmsi()
    {
        var replay = new RadarReplay([], [], _ => null);

        var log = Play(replay, Fix(1, 0, sog: 11.0, status: "At anchor", flags: "R12"));

        Assert.StartsWith("MMSI 219000001 making", Assert.Single(log).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_port_is_named_only_when_the_call_was_plausibly_at_it_and_only_as_the_calls()
    {
        var near = new StoredPortCall
        {
            Mmsi = Mmsi,
            ArrivedUtc = T0,
            DepartedUtc = T0.AddHours(5),
            PortName = "Fredericia",
            PortDistanceNm = 0.4,
        };
        var far = near with { PortName = "Skagen", PortDistanceNm = 14.0 };

        string StopLine(StoredPortCall call)
        {
            var replay = new RadarReplay([Stop(1, 10, 2, 60)], [call], _ => "X");
            return Assert.Single(replay.Advance(Fix(1, 10))).Text;
        }

        Assert.Equal("X stopped on its Fredericia call.", StopLine(near));
        Assert.Equal("X stopped.", StopLine(far));
    }

    [Fact]
    public void Log_lines_print_seconds_because_timestamps_are_never_rounded()
    {
        var entry = new LogEntry(T0.AddSeconds(47), Mmsi, LogKind.Stopped, "X stopped.", new Citation(3, 9));

        Assert.Equal("22:00:47  X stopped. [r3·L9]", entry.Render(withDate: false));
        Assert.Equal("2026-09-04 22:00:47  X stopped. [r3·L9]", entry.Render(withDate: true));
    }
}
