namespace AisPipeline.Adapters.Postgres;

/// <summary>
/// The same schema as the SQLite adapter, in Postgres's dialect.
///
/// Deliberately a separate literal rather than a shared string with substitutions. The two
/// dialects differ in more than syntax -- identity generation, conflict handling, and the type
/// of a timestamp are all different decisions -- and a templated "portable" DDL would hide
/// those behind string replacement while making both harder to read.
/// </summary>
internal static class PostgresSchema
{
    public const string Ddl = """
        CREATE TABLE IF NOT EXISTS ingest_run (
          id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
          source_file TEXT NOT NULL,
          started_utc TIMESTAMPTZ NOT NULL,
          finished_utc TIMESTAMPTZ,
          rows_read BIGINT NOT NULL DEFAULT 0,
          rows_filtered BIGINT NOT NULL DEFAULT 0,
          rows_inserted BIGINT NOT NULL DEFAULT 0,
          rows_dup_in_file BIGINT NOT NULL DEFAULT 0,
          rows_dup_prior_run BIGINT NOT NULL DEFAULT 0,
          rows_quarantined BIGINT NOT NULL DEFAULT 0
        );

        CREATE TABLE IF NOT EXISTS position_report (
          id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
          mmsi BIGINT NOT NULL,
          ts_utc TIMESTAMPTZ NOT NULL,
          lat DOUBLE PRECISION NOT NULL,
          lon DOUBLE PRECISION NOT NULL,
          sog_kn DOUBLE PRECISION,
          cog DOUBLE PRECISION,
          heading DOUBLE PRECISION,
          nav_status TEXT,
          rot DOUBLE PRECISION,
          -- Voyage data, hand-entered and able to go stale on its own. Stored per row
          -- because the feed flattens AIS's static and dynamic messages onto one line,
          -- and this table is the log of what the feed said (ADR-0040).
          draught_m DOUBLE PRECISION,
          destination TEXT,
          eta_utc TIMESTAMPTZ,
          quality_flags TEXT NOT NULL DEFAULT '',
          ingest_run_id BIGINT NOT NULL REFERENCES ingest_run(id),
          source_line BIGINT NOT NULL,
          UNIQUE (mmsi, ts_utc, lat, lon)
        );
        -- No separate (mmsi, ts_utc) index: it is a strict PREFIX of the UNIQUE above, which
        -- Postgres serves from the same B-tree as SQLite does (ADR-0013).
        --
        -- ts_utc is TIMESTAMPTZ here, not TEXT. SQLite has no date type, so it stores a sortable
        -- ISO string; Postgres does, and using it means ORDER BY is a numeric comparison rather
        -- than a string one. The natural key is unaffected: both represent the same instant.

        CREATE TABLE IF NOT EXISTS vessel (
          mmsi BIGINT PRIMARY KEY,
          imo TEXT, name TEXT, callsign TEXT, ship_type TEXT,
          length_m DOUBLE PRECISION, width_m DOUBLE PRECISION,
          first_seen_utc TIMESTAMPTZ NOT NULL,
          last_seen_utc TIMESTAMPTZ NOT NULL
        );

        CREATE TABLE IF NOT EXISTS quarantine (
          id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
          rule_id TEXT NOT NULL,
          source_file TEXT NOT NULL,
          source_line BIGINT NOT NULL,
          raw_snippet TEXT NOT NULL,
          detail TEXT,
          ingest_run_id BIGINT NOT NULL REFERENCES ingest_run(id),
          UNIQUE (source_file, source_line, rule_id)
        );

        CREATE INDEX IF NOT EXISTS ix_quarantine_rule ON quarantine (rule_id);
        -- Rule 2 applied to deletion: rows removed on purpose still leave evidence (ADR-0045).
        CREATE TABLE IF NOT EXISTS retention_event (
          id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
          applied_utc TIMESTAMPTZ NOT NULL,
          cutoff_utc TIMESTAMPTZ NOT NULL,
          fixes_removed BIGINT NOT NULL,
          port_calls_archived BIGINT NOT NULL,
          archive_path TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS stop_event (
          id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
          mmsi BIGINT NOT NULL,
          started_utc TIMESTAMPTZ NOT NULL,
          ended_utc TIMESTAMPTZ NOT NULL,
          duration_hours DOUBLE PRECISION NOT NULL,
          centroid_lat DOUBLE PRECISION NOT NULL,
          centroid_lon DOUBLE PRECISION NOT NULL,
          max_drift_nm DOUBLE PRECISION NOT NULL,
          fix_count INTEGER NOT NULL,
          reliable_fix_count INTEGER NOT NULL,
          geometry_trustworthy BOOLEAN NOT NULL,
          reported_status TEXT,
          status_agrees BOOLEAN NOT NULL,
          is_complete BOOLEAN NOT NULL,
          first_position_id BIGINT NOT NULL REFERENCES position_report(id),
          last_position_id BIGINT NOT NULL REFERENCES position_report(id),
          UNIQUE (mmsi, started_utc)
        );

        CREATE INDEX IF NOT EXISTS ix_stop_vessel_time ON stop_event (mmsi, started_utc);

        CREATE TABLE IF NOT EXISTS port_call (
          id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
          mmsi BIGINT NOT NULL,
          arrived_utc TIMESTAMPTZ NOT NULL,
          departed_utc TIMESTAMPTZ NOT NULL,
          waiting_hours DOUBLE PRECISION NOT NULL,
          working_hours DOUBLE PRECISION NOT NULL,
          unclassified_hours DOUBLE PRECISION NOT NULL,
          centroid_lat DOUBLE PRECISION NOT NULL,
          centroid_lon DOUBLE PRECISION NOT NULL,
          is_complete BOOLEAN NOT NULL,
          -- Nearest port, nullable together: either all four are present or none are. The
          -- distance is stored beside the name because the name alone claims more than a point
          -- gazetteer can support (ADR-0034).
          port_wpi_number INTEGER,
          port_name TEXT,
          port_country TEXT,
          port_distance_nm DOUBLE PRECISION,
          UNIQUE (mmsi, arrived_utc)
        );

        CREATE TABLE IF NOT EXISTS port_call_phase (
          id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
          port_call_id BIGINT NOT NULL REFERENCES port_call(id),
          stop_event_id BIGINT NOT NULL REFERENCES stop_event(id),
          seq INTEGER NOT NULL,
          phase TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_phase_call ON port_call_phase (port_call_id);
        """;

    /// <summary>
    /// Column additions for databases created before a projection grew a column.
    ///
    /// Not part of <see cref="Ddl"/>: CREATE TABLE IF NOT EXISTS silently does nothing when the
    /// table is already there, so a new column has to be added explicitly or older databases fail
    /// on insert with "column does not exist".
    /// </summary>
    public const string ProjectionUpgrades = """
        ALTER TABLE position_report ADD COLUMN IF NOT EXISTS rot DOUBLE PRECISION;
        ALTER TABLE position_report ADD COLUMN IF NOT EXISTS draught_m DOUBLE PRECISION;
        ALTER TABLE position_report ADD COLUMN IF NOT EXISTS destination TEXT;
        ALTER TABLE position_report ADD COLUMN IF NOT EXISTS eta_utc TIMESTAMPTZ;
        ALTER TABLE vessel ADD COLUMN IF NOT EXISTS cargo_type TEXT;
        ALTER TABLE vessel ADD COLUMN IF NOT EXISTS position_fixing_device TEXT;
        ALTER TABLE port_call ADD COLUMN IF NOT EXISTS port_wpi_number INTEGER;
        ALTER TABLE port_call ADD COLUMN IF NOT EXISTS port_name TEXT;
        ALTER TABLE port_call ADD COLUMN IF NOT EXISTS port_country TEXT;
        ALTER TABLE port_call ADD COLUMN IF NOT EXISTS port_distance_nm DOUBLE PRECISION;
        """;
}
