namespace AisPipeline.Adapters.Sqlite;

/// <summary>
/// The schema. Every index here is justified; see docs/adr/0013-drop-redundant-index.md for
/// the one that is deliberately absent.
/// </summary>
internal static class SqliteSchema
{
    public const string Ddl = """
        CREATE TABLE IF NOT EXISTS ingest_run (
          id INTEGER PRIMARY KEY,
          source_file TEXT NOT NULL,
          started_utc TEXT NOT NULL,
          finished_utc TEXT,
          rows_read INTEGER NOT NULL DEFAULT 0,
          rows_filtered INTEGER NOT NULL DEFAULT 0,
          rows_inserted INTEGER NOT NULL DEFAULT 0,
          rows_dup_in_file INTEGER NOT NULL DEFAULT 0,
          rows_dup_prior_run INTEGER NOT NULL DEFAULT 0,
          rows_quarantined INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE IF NOT EXISTS position_report (
          id INTEGER PRIMARY KEY,
          mmsi INTEGER NOT NULL,
          ts_utc TEXT NOT NULL,
          lat REAL NOT NULL,
          lon REAL NOT NULL,
          sog_kn REAL,
          cog REAL,
          heading REAL,
          nav_status TEXT,
          quality_flags TEXT NOT NULL DEFAULT '',
          ingest_run_id INTEGER NOT NULL REFERENCES ingest_run(id),
          source_line INTEGER NOT NULL,
          UNIQUE (mmsi, ts_utc, lat, lon)
        );
        -- No ix_position_vessel_time: (mmsi, ts_utc) is a strict PREFIX of the UNIQUE index
        -- above, so per-vessel time scans are already served. A second index would double the
        -- insert cost for nothing (ADR-0013).

        CREATE TABLE IF NOT EXISTS vessel (
          mmsi INTEGER PRIMARY KEY,
          imo TEXT,
          name TEXT,
          callsign TEXT,
          ship_type TEXT,
          length_m REAL,
          width_m REAL,
          first_seen_utc TEXT NOT NULL,
          last_seen_utc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS quarantine (
          id INTEGER PRIMARY KEY,
          rule_id TEXT NOT NULL,
          source_file TEXT NOT NULL,
          source_line INTEGER NOT NULL,
          raw_snippet TEXT NOT NULL,
          detail TEXT,
          ingest_run_id INTEGER NOT NULL REFERENCES ingest_run(id),
          UNIQUE (source_file, source_line, rule_id)
        );
        -- ingest_run_id is required for the same reason position_report carries it: a refusal
        -- nobody can trace to a run is not evidence. Two runs over the same filename are a
        -- supported operation (ADR-0005), so source_file alone cannot identify which run
        -- refused a row. Combined with INSERT OR IGNORE on the UNIQUE below, the FIRST run to
        -- refuse a row owns it -- matching how position_report keeps its original run id.
        -- The UNIQUE above is what keeps re-ingest idempotent for this table too. Without it,
        -- position_report would stay flat while quarantine doubled (ADR-0006).

        CREATE INDEX IF NOT EXISTS ix_quarantine_rule ON quarantine (rule_id);

        CREATE TABLE IF NOT EXISTS stop_event (
          id INTEGER PRIMARY KEY,
          mmsi INTEGER NOT NULL,
          started_utc TEXT NOT NULL,
          ended_utc TEXT NOT NULL,
          duration_hours REAL NOT NULL,
          centroid_lat REAL NOT NULL,
          centroid_lon REAL NOT NULL,
          max_drift_nm REAL NOT NULL,
          fix_count INTEGER NOT NULL,
          reliable_fix_count INTEGER NOT NULL,
          geometry_trustworthy INTEGER NOT NULL,
          reported_status TEXT,
          status_agrees INTEGER NOT NULL,
          is_complete INTEGER NOT NULL,
          first_position_id INTEGER NOT NULL REFERENCES position_report(id),
          last_position_id INTEGER NOT NULL REFERENCES position_report(id),
          UNIQUE (mmsi, started_utc)
        );
        -- duration_hours is only meaningful when is_complete = 1. A stop touching a coverage
        -- gap or the edge of the ingested window has an unknown true length (ADR-0011).

        -- max_drift_nm means nothing when geometry_trustworthy = 0: too few fixes survived
        -- exclusion for a spread to be measurable, and with one survivor the centroid IS that
        -- fix, so drift computes to exactly 0.0 (ADR-0025).

        CREATE INDEX IF NOT EXISTS ix_stop_vessel_time ON stop_event (mmsi, started_utc);
        -- Not redundant, unlike the position index dropped in ADR-0013: port-call chaining
        -- walks one vessel's stops in order, and this index and the UNIQUE above coincide.

        CREATE TABLE IF NOT EXISTS port_call (
          id INTEGER PRIMARY KEY,
          mmsi INTEGER NOT NULL,
          arrived_utc TEXT NOT NULL,
          departed_utc TEXT NOT NULL,
          waiting_hours REAL NOT NULL,
          working_hours REAL NOT NULL,
          unclassified_hours REAL NOT NULL,
          centroid_lat REAL NOT NULL,
          centroid_lon REAL NOT NULL,
          is_complete INTEGER NOT NULL,
          -- Nearest port, nullable together: either all four are present or none are. The
          -- distance is stored beside the name because the name alone claims more than a point
          -- gazetteer can support (ADR-0034).
          port_wpi_number INTEGER,
          port_name TEXT,
          port_country TEXT,
          port_distance_nm REAL,
          UNIQUE (mmsi, arrived_utc)
        );

        CREATE TABLE IF NOT EXISTS port_call_phase (
          id INTEGER PRIMARY KEY,
          port_call_id INTEGER NOT NULL REFERENCES port_call(id),
          stop_event_id INTEGER NOT NULL REFERENCES stop_event(id),
          seq INTEGER NOT NULL,
          phase TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_phase_call ON port_call_phase (port_call_id);
        """;

    /// <summary>
    /// WAL and NORMAL synchronous are the standard batch-ingest settings. synchronous=OFF was
    /// not chosen: it trades durability for speed, and a torn database after a crash would
    /// leave rows whose provenance does not resolve.
    /// </summary>
    public const string Pragmas = """
        PRAGMA journal_mode=WAL;
        PRAGMA synchronous=NORMAL;
        PRAGMA temp_store=MEMORY;
        """;
}
