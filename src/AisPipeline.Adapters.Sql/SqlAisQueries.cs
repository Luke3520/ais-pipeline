using System.Data.Common;
using AisPipeline.Core.Domain;
using AisPipeline.Core.Ports;
using AisPipeline.Core.Query;
using Dapper;

namespace AisPipeline.Adapters.Sql;

/// <summary>
/// The read side, implemented once for every relational adapter.
///
/// This is a deliberate contrast with the schema, which ADR-0026 writes out twice. DDL genuinely
/// differs between the engines -- identity generation, conflict handling, the type of a timestamp
/// are separate decisions. These queries are plain ANSI SELECTs over the same tables, and Dapper
/// normalises the one place the providers disagree on syntax (named parameters, and expanding a
/// collection into an IN list).
///
/// The claim that one implementation serves both is exactly the kind of thing that is easy to
/// assert and easy to get wrong at the edges -- boolean storage, timestamp round-tripping -- so
/// the query suite runs against both engines rather than one (ADR-0027).
/// </summary>
public sealed class SqlAisQueries : IAisQueries
{
    private readonly Func<DbConnection> _connect;
    private readonly SqlDialect _dialect;

    static SqlAisQueries() => UtcDateTimeHandler.Register();

    public SqlAisQueries(Func<DbConnection> connect, SqlDialect dialect)
    {
        _connect = connect;
        _dialect = dialect;
    }

    /// <summary>
    /// Rows scanned by this instance, for the N+1 tests.
    ///
    /// Counting queries is the only way to prove a batched resolver is actually batched: a
    /// GraphQL response looks identical whether it took one round trip or a hundred, and
    /// wall-clock timing on a small dataset does not distinguish them reliably (ADR-0027).
    /// </summary>
    public int QueryCount { get; private set; }

    private DbConnection Open()
    {
        QueryCount++;
        var connection = _connect();
        connection.Open();
        return connection;
    }

    public StoredVessel? GetVessel(long mmsi)
    {
        using var c = Open();
        return c.QuerySingleOrDefault<StoredVessel>(
            $"{VesselColumns} WHERE mmsi = @mmsi", new { mmsi });
    }

    public IReadOnlyList<StoredVessel> GetVessels(IReadOnlyCollection<long> mmsis)
    {
        if (mmsis.Count == 0)
        {
            return [];
        }

        using var c = Open();
        return [.. c.Query<StoredVessel>(
            $"{VesselColumns} WHERE {_dialect.InList("mmsi", "@mmsis")}",
            new { mmsis = Ids(mmsis) })];
    }

    public IReadOnlyList<StoredVessel> ListVessels(string? shipType, int limit)
    {
        using var c = Open();
        return [.. c.Query<StoredVessel>($"""
            {VesselColumns}
            WHERE (@shipType IS NULL OR ship_type = @shipType)
            ORDER BY mmsi
            LIMIT @limit
            """, new { shipType, limit = Clamp(limit) })];
    }

    public IReadOnlyList<StoredStop> ListStops(StopFilter filter)
    {
        using var c = Open();
        return [.. c.Query<StoredStop>($"""
            {StopColumns}
            WHERE (@mmsi IS NULL OR mmsi = @mmsi)
              AND (@minHours IS NULL OR duration_hours >= @minHours)
              AND (@completeOnly = 0 OR {_dialect.IsTrue("is_complete")})
              AND (@disagreementsOnly = 0 OR {_dialect.IsFalse("status_agrees")})
            ORDER BY duration_hours DESC, mmsi, started_utc
            LIMIT @limit
            """, new
        {
            mmsi = filter.Mmsi,
            minHours = filter.MinHours,
            completeOnly = filter.CompleteOnly ? 1 : 0,
            disagreementsOnly = filter.DisagreementsOnly ? 1 : 0,
            limit = Clamp(filter.Limit),
        })];
    }

    public IReadOnlyList<StoredStop> GetStopsForVessels(IReadOnlyCollection<long> mmsis, int limitPerVessel)
    {
        if (mmsis.Count == 0)
        {
            return [];
        }

        using var c = Open();

        // One query for every vessel asked for, rather than one per vessel. The per-vessel cap is
        // applied client-side: a windowed LIMIT would need a lateral join or a window function,
        // and the row counts here are small enough that the round trip dominates.
        var all = c.Query<StoredStop>($"""
            {StopColumns}
            WHERE {_dialect.InList("mmsi", "@mmsis")}
            ORDER BY mmsi, started_utc
            """, new { mmsis = Ids(mmsis) });

        return [.. all
            .GroupBy(s => s.Mmsi)
            .SelectMany(g => g.Take(Clamp(limitPerVessel)))];
    }

    public IReadOnlyList<StoredPortCall> ListPortCalls(PortCallFilter filter)
    {
        using var c = Open();
        return [.. c.Query<StoredPortCall>($"""
            {PortCallColumns}
            WHERE (@mmsi IS NULL OR mmsi = @mmsi)
              AND (@minWaiting IS NULL OR waiting_hours >= @minWaiting)
              AND (@completeOnly = 0 OR {_dialect.IsTrue("is_complete")})
            ORDER BY (waiting_hours + working_hours) DESC, mmsi, arrived_utc
            LIMIT @limit
            """, new
        {
            mmsi = filter.Mmsi,
            minWaiting = filter.MinWaitingHours,
            completeOnly = filter.CompleteOnly ? 1 : 0,
            limit = Clamp(filter.Limit),
        })];
    }

    public IReadOnlyList<StoredPortCall> GetPortCallsForVessels(
        IReadOnlyCollection<long> mmsis, int limitPerVessel)
    {
        if (mmsis.Count == 0)
        {
            return [];
        }

        using var c = Open();
        var all = c.Query<StoredPortCall>($"""
            {PortCallColumns}
            WHERE {_dialect.InList("mmsi", "@mmsis")}
            ORDER BY mmsi, arrived_utc DESC
            """, new { mmsis = Ids(mmsis) });

        // Most recent first, so a cap drops the oldest rather than the newest. Ascending order
        // with a Take() kept the earliest calls and discarded everything after -- the opposite of
        // what a caller asking for a vessel's port calls wants (ADR-0028).
        return [.. all
            .GroupBy(p => p.Mmsi)
            .SelectMany(g => g.Take(Clamp(limitPerVessel)))];
    }

    public IReadOnlyDictionary<long, long> CountPortCallsForVessels(IReadOnlyCollection<long> mmsis)
    {
        if (mmsis.Count == 0)
        {
            return new Dictionary<long, long>();
        }

        using var c = Open();
        return c.Query<(long Mmsi, long Total)>($"""
            SELECT mmsi AS Mmsi, COUNT(*) AS Total
            FROM port_call
            WHERE {_dialect.InList("mmsi", "@mmsis")}
            GROUP BY mmsi
            """, new { mmsis = Ids(mmsis) })
            .ToDictionary(r => r.Mmsi, r => r.Total);
    }

    public IReadOnlyList<(StoredPhase Phase, StoredStop Stop)> GetPhasesForPortCalls(
        IReadOnlyCollection<long> portCallIds)
    {
        if (portCallIds.Count == 0)
        {
            return [];
        }

        using var c = Open();

        // Phases and their stops in one query. Resolving a port call's phases and then each
        // phase's stop separately is the N+1 one level deeper than the vessel case.
        var rows = c.Query<StoredPhase, StoredStop, (StoredPhase, StoredStop)>($"""
            SELECT p.id AS Id, p.port_call_id AS PortCallId, p.stop_event_id AS StopId,
                   p.seq AS Sequence, p.phase AS Phase,
                   {StopSelectList("s")}
            FROM port_call_phase p
            JOIN stop_event s ON s.id = p.stop_event_id
            WHERE {_dialect.InList("p.port_call_id", "@ids")}
            ORDER BY p.port_call_id, p.seq
            """, (phase, stop) => (phase, stop), new { ids = Ids(portCallIds) }, splitOn: "Id");

        return [.. rows];
    }

    public IReadOnlyList<PositionFix> ListFixes(long mmsi, DateTime fromUtc, DateTime toUtc, int limit)
    {
        using var c = Open();
        return [.. c.Query<PositionFix>("""
            SELECT id AS Id, mmsi AS Mmsi, ts_utc AS TimestampUtc, lat AS Latitude,
                   lon AS Longitude, sog_kn AS SpeedOverGroundKn, nav_status AS NavigationalStatus,
                   quality_flags AS QualityFlags
            FROM position_report
            WHERE mmsi = @mmsi AND ts_utc >= @fromUtc AND ts_utc <= @toUtc
            ORDER BY ts_utc, id
            LIMIT @limit
            """, new { mmsi, fromUtc, toUtc, limit = Clamp(limit) })];
    }

    public IReadOnlyList<RuleHitCount> QualityReport()
    {
        using var c = Open();
        return [.. c.Query<RuleHitCount>("""
            SELECT rule_id AS RuleId, COUNT(*) AS Quarantined
            FROM quarantine
            GROUP BY rule_id
            ORDER BY rule_id
            """)];
    }

    public IReadOnlyList<StoredRun> ListRuns()
    {
        using var c = Open();
        return [.. c.Query<StoredRun>("""
            SELECT id AS Id, source_file AS SourceFile, started_utc AS StartedUtc,
                   finished_utc AS FinishedUtc, rows_read AS RowsRead,
                   rows_filtered AS RowsFiltered, rows_inserted AS RowsInserted,
                   rows_dup_in_file AS RowsDuplicateInFile,
                   rows_dup_prior_run AS RowsDuplicatePriorRun,
                   rows_quarantined AS RowsQuarantined
            FROM ingest_run
            ORDER BY id
            """)];
    }

    public void Dispose()
    {
        // Connections are opened and closed per query; nothing is held.
    }

    /// <summary>
    /// An array, so Npgsql can bind it to a bigint[] for <c>= ANY</c>. SQLite's path is Dapper's
    /// IN expansion, which takes any collection.
    /// </summary>
    private static long[] Ids(IReadOnlyCollection<long> ids) => [.. ids];

    /// <summary>An unbounded LIMIT from a query string is a denial of service with extra steps.</summary>
    private static int Clamp(int limit) => Math.Clamp(limit, 1, 1_000);

    private const string VesselColumns = """
        SELECT mmsi AS Mmsi, imo AS Imo, name AS Name, callsign AS CallSign,
               ship_type AS ShipType, length_m AS LengthM, width_m AS WidthM,
               first_seen_utc AS FirstSeenUtc, last_seen_utc AS LastSeenUtc
        FROM vessel
        """;

    private static string StopSelectList(string alias) => $"""
        {alias}.id AS Id, {alias}.mmsi AS Mmsi, {alias}.started_utc AS StartedUtc,
        {alias}.ended_utc AS EndedUtc, {alias}.duration_hours AS ObservedDurationHours,
        {alias}.centroid_lat AS CentroidLatitude, {alias}.centroid_lon AS CentroidLongitude,
        {alias}.max_drift_nm AS ObservedMaxDriftNm, {alias}.fix_count AS FixCount,
        {alias}.reliable_fix_count AS ReliableFixCount,
        {alias}.geometry_trustworthy AS GeometryTrustworthy,
        {alias}.reported_status AS ReportedStatus, {alias}.status_agrees AS StatusAgrees,
        {alias}.is_complete AS IsComplete, {alias}.first_position_id AS FirstPositionId,
        {alias}.last_position_id AS LastPositionId
        """;

    private static readonly string StopColumns = $"SELECT {StopSelectList("stop_event")} FROM stop_event";

    private const string PortCallColumns = """
        SELECT id AS Id, mmsi AS Mmsi, arrived_utc AS ArrivedUtc, departed_utc AS DepartedUtc,
               waiting_hours AS WaitingHours, working_hours AS WorkingHours,
               unclassified_hours AS UnclassifiedHours, centroid_lat AS CentroidLatitude,
               centroid_lon AS CentroidLongitude, is_complete AS IsComplete
        FROM port_call
        """;
}
