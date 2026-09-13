using System.Globalization;
using AisPipeline.Core.Annotate;
using AisPipeline.Core.Detection;
using AisPipeline.Core.Domain;
using AisPipeline.Core.Ingest;
using AisPipeline.Core.Ports;
using Microsoft.Data.Sqlite;

namespace AisPipeline.Adapters.Sqlite;

/// <summary>
/// SQLite implementation of <see cref="IAisStore"/>.
///
/// Writes go through reused prepared commands inside explicit transactions: the hot path is
/// millions of inserts, and re-parsing the same SQL per row dominates everything else.
/// </summary>
public sealed class SqliteAisStore : IAisStore
{
    /// <summary>
    /// Round-trip format for timestamps. Sortable as text, which is what makes ORDER BY ts_utc
    /// correct in a store with no native date type, and invariant by construction.
    /// </summary>
    internal const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    private readonly SqliteConnection _connection;

    public SqliteAisStore(string databasePath)
    {
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());

        _connection.Open();
        Execute(SqliteSchema.Pragmas);
    }

    internal static string Format(DateTime utc) =>
        utc.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture);

    public void EnsureSchema()
    {
        Execute(SqliteSchema.Ddl);
        AddMissingColumns();
    }

    /// <summary>
    /// Columns added to a table after rows already existed, with the release that added them.
    ///
    /// CREATE TABLE IF NOT EXISTS does nothing when the table is there, so a column added later is
    /// invisible to an existing database and every insert fails on "no such column". SQLite has no
    /// ADD COLUMN IF NOT EXISTS, which is why each one is checked before it is added rather than
    /// attempted and swallowed -- a swallowed error hides the case where the ALTER failed for some
    /// other reason.
    ///
    /// ALTER rather than a rebuild. position_report is the immutable log and cannot be recomputed
    /// from anything; the new columns read null on rows ingested before they existed, which is
    /// exactly what null means here. Re-ingesting the same files will not fill them either --
    /// ingest is idempotent by natural key, so the rows are skipped (ADR-0038, ADR-0040).
    /// </summary>
    private static readonly (string Table, string Column, string Type)[] AddedColumns =
    [
        ("port_call", "port_wpi_number", "INTEGER"),
        ("port_call", "port_name", "TEXT"),
        ("port_call", "port_country", "TEXT"),
        ("port_call", "port_distance_nm", "REAL"),
        ("position_report", "rot", "REAL"),
        ("position_report", "draught_m", "REAL"),
        ("position_report", "destination", "TEXT"),
        ("position_report", "eta_utc", "TEXT"),
        ("vessel", "cargo_type", "TEXT"),
        ("vessel", "position_fixing_device", "TEXT"),
    ];

    private void AddMissingColumns()
    {
        foreach (var (table, column, type) in AddedColumns)
        {
            using var check = _connection.CreateCommand();
            check.CommandText =
                $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}';";

            if ((long)check.ExecuteScalar()! > 0)
            {
                continue;
            }

            using var alter = _connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type};";
            alter.ExecuteNonQuery();
        }
    }

    public long BeginRun(string sourceFile, DateTime startedUtc)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ingest_run (source_file, started_utc) VALUES ($file, $started);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$file", sourceFile);
        command.Parameters.AddWithValue("$started", Format(startedUtc));
        return (long)command.ExecuteScalar()!;
    }

    public void CompleteRun(long runId, DateTime finishedUtc, IngestCounters counters)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            UPDATE ingest_run SET
              finished_utc = $finished,
              rows_read = $read,
              rows_filtered = $filtered,
              rows_inserted = $inserted,
              rows_dup_in_file = $dupFile,
              rows_dup_prior_run = $dupPrior,
              rows_quarantined = $quarantined
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$finished", Format(finishedUtc));
        command.Parameters.AddWithValue("$read", counters.RowsRead);
        command.Parameters.AddWithValue("$filtered", counters.RowsFiltered);
        command.Parameters.AddWithValue("$inserted", counters.RowsInserted);
        command.Parameters.AddWithValue("$dupFile", counters.RowsDuplicateInFile);
        command.Parameters.AddWithValue("$dupPrior", counters.RowsDuplicatePriorRun);
        command.Parameters.AddWithValue("$quarantined", counters.RowsQuarantined);
        command.Parameters.AddWithValue("$id", runId);
        command.ExecuteNonQuery();
    }

    public int InsertPositions(long runId, IReadOnlyList<AcceptedPosition> batch)
    {
        if (batch.Count == 0)
        {
            return 0;
        }

        using var transaction = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO position_report
              (mmsi, ts_utc, lat, lon, sog_kn, cog, heading, nav_status, rot,
               draught_m, destination, eta_utc, quality_flags, ingest_run_id, source_line)
            VALUES ($mmsi, $ts, $lat, $lon, $sog, $cog, $heading, $nav, $rot,
                    $draught, $dest, $eta, $flags, $run, $line);
            """;

        var mmsi = command.Parameters.Add("$mmsi", SqliteType.Integer);
        var ts = command.Parameters.Add("$ts", SqliteType.Text);
        var lat = command.Parameters.Add("$lat", SqliteType.Real);
        var lon = command.Parameters.Add("$lon", SqliteType.Real);
        var sog = command.Parameters.Add("$sog", SqliteType.Real);
        var cog = command.Parameters.Add("$cog", SqliteType.Real);
        var heading = command.Parameters.Add("$heading", SqliteType.Real);
        var nav = command.Parameters.Add("$nav", SqliteType.Text);
        var rot = command.Parameters.Add("$rot", SqliteType.Real);
        var draught = command.Parameters.Add("$draught", SqliteType.Real);
        var dest = command.Parameters.Add("$dest", SqliteType.Text);
        var eta = command.Parameters.Add("$eta", SqliteType.Text);
        var flags = command.Parameters.Add("$flags", SqliteType.Text);
        var run = command.Parameters.Add("$run", SqliteType.Integer);
        var line = command.Parameters.Add("$line", SqliteType.Integer);
        run.Value = runId;
        command.Prepare();

        var inserted = 0;
        foreach (var accepted in batch)
        {
            var record = accepted.Record;
            mmsi.Value = record.Mmsi;
            ts.Value = Format(record.TimestampUtc);
            lat.Value = record.Latitude;
            lon.Value = record.Longitude;
            sog.Value = (object?)record.SpeedOverGroundKn ?? DBNull.Value;
            cog.Value = (object?)record.CourseOverGround ?? DBNull.Value;
            heading.Value = (object?)record.HeadingDegrees ?? DBNull.Value;
            nav.Value = record.NavigationalStatus;
            rot.Value = (object?)record.RateOfTurnDegPerMin ?? DBNull.Value;
            draught.Value = (object?)record.DraughtM ?? DBNull.Value;
            dest.Value = (object?)record.Destination ?? DBNull.Value;
            eta.Value = record.EtaUtc is { } etaUtc ? Format(etaUtc) : DBNull.Value;
            flags.Value = accepted.QualityFlags;
            line.Value = record.SourceLine;

            // INSERT OR IGNORE reports 0 when the natural key is already present, which is how
            // the caller separates newly stored rows from duplicates (ADR-0005).
            inserted += command.ExecuteNonQuery();
        }

        transaction.Commit();
        return inserted;
    }

    public void InsertQuarantine(long runId, IReadOnlyList<QuarantinedRow> rows)
    {
        if (rows.Count == 0)
        {
            return;
        }

        using var transaction = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO quarantine
              (rule_id, source_file, source_line, raw_snippet, detail, ingest_run_id)
            VALUES ($rule, $file, $line, $raw, $detail, $run);
            """;

        var rule = command.Parameters.Add("$rule", SqliteType.Text);
        var file = command.Parameters.Add("$file", SqliteType.Text);
        var line = command.Parameters.Add("$line", SqliteType.Integer);
        var raw = command.Parameters.Add("$raw", SqliteType.Text);
        var detail = command.Parameters.Add("$detail", SqliteType.Text);
        var run = command.Parameters.Add("$run", SqliteType.Integer);
        run.Value = runId;
        command.Prepare();

        foreach (var row in rows)
        {
            rule.Value = row.Hit.RuleId;
            file.Value = row.SourceFile;
            line.Value = row.SourceLine;
            raw.Value = row.RawText;
            detail.Value = row.Hit.Detail;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public void UpsertVessels(IReadOnlyList<Vessel> vessels)
    {
        if (vessels.Count == 0)
        {
            return;
        }

        using var transaction = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;

        // COALESCE on the excluded value keeps a previously known identity when a later run
        // carries none: static data rides only on message-type-5 rows, so most rows have none
        // and overwriting with null would erase what an earlier file established (ADR-0007).
        command.CommandText = """
            INSERT INTO vessel (mmsi, imo, name, callsign, ship_type, cargo_type,
                                position_fixing_device, length_m, width_m,
                                first_seen_utc, last_seen_utc)
            VALUES ($mmsi, $imo, $name, $callsign, $type, $cargo, $fixing,
                    $length, $width, $first, $last)
            ON CONFLICT (mmsi) DO UPDATE SET
              imo = COALESCE(excluded.imo, vessel.imo),
              name = COALESCE(excluded.name, vessel.name),
              callsign = COALESCE(excluded.callsign, vessel.callsign),
              ship_type = COALESCE(excluded.ship_type, vessel.ship_type),
              cargo_type = COALESCE(excluded.cargo_type, vessel.cargo_type),
              position_fixing_device =
                  COALESCE(excluded.position_fixing_device, vessel.position_fixing_device),
              length_m = COALESCE(excluded.length_m, vessel.length_m),
              width_m = COALESCE(excluded.width_m, vessel.width_m),
              first_seen_utc = MIN(excluded.first_seen_utc, vessel.first_seen_utc),
              last_seen_utc = MAX(excluded.last_seen_utc, vessel.last_seen_utc);
            """;

        var mmsi = command.Parameters.Add("$mmsi", SqliteType.Integer);
        var imo = command.Parameters.Add("$imo", SqliteType.Text);
        var name = command.Parameters.Add("$name", SqliteType.Text);
        var callsign = command.Parameters.Add("$callsign", SqliteType.Text);
        var type = command.Parameters.Add("$type", SqliteType.Text);
        var cargo = command.Parameters.Add("$cargo", SqliteType.Text);
        var fixing = command.Parameters.Add("$fixing", SqliteType.Text);
        var length = command.Parameters.Add("$length", SqliteType.Real);
        var width = command.Parameters.Add("$width", SqliteType.Real);
        var first = command.Parameters.Add("$first", SqliteType.Text);
        var last = command.Parameters.Add("$last", SqliteType.Text);
        command.Prepare();

        foreach (var vessel in vessels)
        {
            mmsi.Value = vessel.Mmsi;
            imo.Value = (object?)vessel.Imo ?? DBNull.Value;
            name.Value = (object?)vessel.Name ?? DBNull.Value;
            callsign.Value = (object?)vessel.CallSign ?? DBNull.Value;
            type.Value = (object?)vessel.ShipType ?? DBNull.Value;
            cargo.Value = (object?)vessel.CargoType ?? DBNull.Value;
            fixing.Value = (object?)vessel.PositionFixingDevice ?? DBNull.Value;
            length.Value = (object?)vessel.LengthM ?? DBNull.Value;
            width.Value = (object?)vessel.WidthM ?? DBNull.Value;
            first.Value = Format(vessel.FirstSeenUtc);
            last.Value = Format(vessel.LastSeenUtc);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public IEnumerable<PositionFix> ReadFixesOrdered()
    {
        using var command = _connection.CreateCommand();

        // The ORDER BY is served by the prefix of UNIQUE (mmsi, ts_utc, lat, lon), which is why
        // no separate (mmsi, ts_utc) index exists (ADR-0013). id is the tiebreak so the order is
        // total: two receivers reporting the same vessel-second at different positions would
        // otherwise sort arbitrarily, and a non-deterministic order makes re-running detection
        // produce different stops from identical data.
        command.CommandText = """
            SELECT id, mmsi, ts_utc, lat, lon, sog_kn, nav_status, quality_flags
            FROM position_report
            ORDER BY mmsi, ts_utc, id;
            """;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            yield return new PositionFix
            {
                Id = reader.GetInt64(0),
                Mmsi = reader.GetInt64(1),
                TimestampUtc = ParseTimestamp(reader.GetString(2)),
                Latitude = reader.GetDouble(3),
                Longitude = reader.GetDouble(4),
                SpeedOverGroundKn = reader.IsDBNull(5) ? null : reader.GetDouble(5),
                NavigationalStatus = reader.IsDBNull(6) ? null : reader.GetString(6),
                QualityFlags = reader.GetString(7),
            };
        }
    }

    public void UpdateQualityFlags(IReadOnlyList<FlagUpdate> updates)
    {
        if (updates.Count == 0)
        {
            return;
        }

        using var transaction = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE position_report SET quality_flags = $flags WHERE id = $id;";
        var flags = command.Parameters.Add("$flags", SqliteType.Text);
        var id = command.Parameters.Add("$id", SqliteType.Integer);
        command.Prepare();

        foreach (var update in updates)
        {
            flags.Value = update.QualityFlags;
            id.Value = update.PositionId;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public void ReplaceDetections(IReadOnlyList<PortCall> portCalls)
    {
        using var transaction = _connection.BeginTransaction();

        // Delete before insert, inside the same transaction. These are projections over
        // position_report, so a partial replace would leave a mixture of two computations --
        // and re-running detection has to land on exactly the previous result (ADR-0009).
        //
        // Emptied, not dropped. Dropping and recreating here would also fix a stale SHAPE, but it
        // churns the schema on every run: measured, it turned a suite that passed 40 times out of
        // 40 into one that failed roughly one run in forty, because a schema change invalidates
        // statements cached on pooled connections. Shape is EnsureSchema's job, once.
        //
        // Phases first: they reference both of the tables below.
        foreach (var table in new[] { "port_call_phase", "port_call", "stop_event" })
        {
            using var delete = _connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = $"DELETE FROM {table};";
            delete.ExecuteNonQuery();
        }

        foreach (var call in portCalls)
        {
            var callId = InsertPortCall(transaction, call);

            foreach (var phase in call.Phases)
            {
                var stopId = InsertStop(transaction, phase.Stop);
                InsertPhase(transaction, callId, stopId, phase);
            }
        }

        transaction.Commit();
    }

    private long InsertPortCall(SqliteTransaction transaction, PortCall call)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO port_call (mmsi, arrived_utc, departed_utc, waiting_hours, working_hours,
                                   unclassified_hours, centroid_lat, centroid_lon, is_complete,
                                   port_wpi_number, port_name, port_country, port_distance_nm)
            VALUES ($mmsi, $arrived, $departed, $waiting, $working, $unclassified,
                    $lat, $lon, $complete, $portWpi, $portName, $portCountry, $portNm);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$mmsi", call.Mmsi);
        command.Parameters.AddWithValue("$arrived", Format(call.ArrivedUtc));
        command.Parameters.AddWithValue("$departed", Format(call.DepartedUtc));
        command.Parameters.AddWithValue("$waiting", call.WaitingHours);
        command.Parameters.AddWithValue("$working", call.WorkingHours);
        command.Parameters.AddWithValue("$unclassified", call.UnclassifiedHours);
        command.Parameters.AddWithValue("$lat", call.CentroidLatitude);
        command.Parameters.AddWithValue("$lon", call.CentroidLongitude);
        command.Parameters.AddWithValue("$complete", call.IsComplete ? 1 : 0);
        command.Parameters.AddWithValue("$portWpi", (object?)call.Attribution?.WpiNumber ?? DBNull.Value);
        command.Parameters.AddWithValue("$portName", (object?)call.Attribution?.Name ?? DBNull.Value);
        command.Parameters.AddWithValue("$portCountry", (object?)call.Attribution?.Country ?? DBNull.Value);
        command.Parameters.AddWithValue("$portNm", (object?)call.Attribution?.DistanceNm ?? DBNull.Value);
        return (long)command.ExecuteScalar()!;
    }

    private long InsertStop(SqliteTransaction transaction, StopEvent stop)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO stop_event (mmsi, started_utc, ended_utc, duration_hours, centroid_lat,
                                    centroid_lon, max_drift_nm, fix_count, reliable_fix_count,
                                    geometry_trustworthy, reported_status, status_agrees,
                                    is_complete, first_position_id, last_position_id)
            VALUES ($mmsi, $started, $ended, $duration, $lat, $lon, $drift, $fixes, $reliable,
                    $trustworthy, $status, $agrees, $complete, $first, $last);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$mmsi", stop.Mmsi);
        command.Parameters.AddWithValue("$started", Format(stop.StartedUtc));
        command.Parameters.AddWithValue("$ended", Format(stop.EndedUtc));
        command.Parameters.AddWithValue("$duration", stop.DurationHours);
        command.Parameters.AddWithValue("$lat", stop.CentroidLatitude);
        command.Parameters.AddWithValue("$lon", stop.CentroidLongitude);
        command.Parameters.AddWithValue("$drift", stop.MaxDriftNm);
        command.Parameters.AddWithValue("$fixes", stop.FixCount);
        command.Parameters.AddWithValue("$reliable", stop.ReliableFixCount);
        command.Parameters.AddWithValue("$trustworthy", stop.GeometryTrustworthy ? 1 : 0);
        command.Parameters.AddWithValue("$status", (object?)stop.ReportedStatus ?? DBNull.Value);
        command.Parameters.AddWithValue("$agrees", stop.StatusAgrees ? 1 : 0);
        command.Parameters.AddWithValue("$complete", stop.IsComplete ? 1 : 0);
        command.Parameters.AddWithValue("$first", stop.FirstPositionId);
        command.Parameters.AddWithValue("$last", stop.LastPositionId);
        return (long)command.ExecuteScalar()!;
    }

    private void InsertPhase(SqliteTransaction transaction, long callId, long stopId, PortCallPhase phase)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO port_call_phase (port_call_id, stop_event_id, seq, phase)
            VALUES ($call, $stop, $seq, $phase);
            """;
        command.Parameters.AddWithValue("$call", callId);
        command.Parameters.AddWithValue("$stop", stopId);
        command.Parameters.AddWithValue("$seq", phase.Sequence);
        command.Parameters.AddWithValue("$phase", phase.Phase.ToString());
        command.ExecuteNonQuery();
    }

    private static DateTime ParseTimestamp(string text) =>
        DateTime.ParseExact(text, TimestampFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    public void Dispose() => _connection.Dispose();

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
