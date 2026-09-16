using AisPipeline.Core.Annotate;
using AisPipeline.Core.Domain;
using AisPipeline.Core.Ingest;
using AisPipeline.Core.Ports;
using Npgsql;
using NpgsqlTypes;

namespace AisPipeline.Adapters.Postgres;

/// <summary>
/// Postgres implementation of <see cref="IAisStore"/>.
///
/// The same ports, a different engine. Where this differs from the SQLite adapter it is because
/// the dialects genuinely differ, not because the domain does:
///
/// <list type="bullet">
/// <item>ON CONFLICT DO NOTHING rather than INSERT OR IGNORE.</item>
/// <item>RETURNING id rather than last_insert_rowid().</item>
/// <item>TIMESTAMPTZ rather than an ISO string, so ORDER BY compares instants not text.</item>
/// <item>Binary COPY for the position hot path -- see <see cref="InsertPositions"/>.</item>
/// </list>
/// </summary>
public sealed class PostgresAisStore : IAisStore
{
    private readonly NpgsqlDataSource _source;
    private readonly NpgsqlConnection _connection;

    public PostgresAisStore(string connectionString)
    {
        _source = NpgsqlDataSource.Create(connectionString);
        _connection = _source.OpenConnection();
    }

    public void EnsureSchema()
    {
        Execute(PostgresSchema.Ddl);

        // The port columns on a port_call built before ADR-0034. Postgres has ADD COLUMN IF NOT
        // EXISTS, so no guard query is needed -- unlike SQLite, which is why the two adapters do
        // this differently. Harmless to re-run, and ALTER rather than a rebuild keeps the
        // detections already there (they read null until the next detect, which is what null
        // means here).
        Execute(PostgresSchema.ProjectionUpgrades);
    }

    public long BeginRun(string sourceFile, DateTime startedUtc)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            "INSERT INTO ingest_run (source_file, started_utc) VALUES ($1, $2) RETURNING id;";
        command.Parameters.AddWithValue(sourceFile);
        command.Parameters.AddWithValue(Utc(startedUtc));
        return (long)command.ExecuteScalar()!;
    }

    public void CompleteRun(long runId, DateTime finishedUtc, IngestCounters counters)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            UPDATE ingest_run SET finished_utc = $1, rows_read = $2, rows_filtered = $3,
              rows_inserted = $4, rows_dup_in_file = $5, rows_dup_prior_run = $6,
              rows_quarantined = $7
            WHERE id = $8;
            """;
        command.Parameters.AddWithValue(Utc(finishedUtc));
        command.Parameters.AddWithValue(counters.RowsRead);
        command.Parameters.AddWithValue(counters.RowsFiltered);
        command.Parameters.AddWithValue(counters.RowsInserted);
        command.Parameters.AddWithValue(counters.RowsDuplicateInFile);
        command.Parameters.AddWithValue(counters.RowsDuplicatePriorRun);
        command.Parameters.AddWithValue(counters.RowsQuarantined);
        command.Parameters.AddWithValue(runId);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Inserts a batch and reports how many rows were genuinely new.
    ///
    /// COPY is the fast path in Postgres and would be the obvious choice for millions of rows,
    /// but it cannot express ON CONFLICT DO NOTHING -- it aborts the whole batch on a duplicate
    /// key. Since 38% of a DMA file is duplicate on first ingest (ADR-0005), COPY straight into
    /// position_report would fail on essentially every batch.
    ///
    /// So: COPY into an unlogged staging table, then move rows across with ON CONFLICT DO
    /// NOTHING and count what landed. That keeps the bulk-load speed and the exact insert count
    /// the duplicate accounting depends on (ADR-0012).
    /// </summary>
    public int InsertPositions(long runId, IReadOnlyList<AcceptedPosition> batch)
    {
        if (batch.Count == 0)
        {
            return 0;
        }

        using var transaction = _connection.BeginTransaction();

        Execute("""
            CREATE TEMP TABLE IF NOT EXISTS staging_position (
              mmsi BIGINT, ts_utc TIMESTAMPTZ, lat DOUBLE PRECISION, lon DOUBLE PRECISION,
              sog_kn DOUBLE PRECISION, cog DOUBLE PRECISION, heading DOUBLE PRECISION,
              nav_status TEXT, rot DOUBLE PRECISION, draught_m DOUBLE PRECISION,
              destination TEXT, eta_utc TIMESTAMPTZ,
              quality_flags TEXT, source_line BIGINT, ord BIGINT
            ) ON COMMIT DROP;
            """, transaction);

        using (var writer = _connection.BeginBinaryImport("""
            COPY staging_position (mmsi, ts_utc, lat, lon, sog_kn, cog, heading,
                                   nav_status, rot, draught_m, destination, eta_utc,
                                   quality_flags, source_line, ord)
            FROM STDIN (FORMAT BINARY);
            """))
        {
            for (var i = 0; i < batch.Count; i++)
            {
                var accepted = batch[i];
                var r = accepted.Record;
                writer.StartRow();
                writer.Write(r.Mmsi, NpgsqlDbType.Bigint);
                writer.Write(Utc(r.TimestampUtc), NpgsqlDbType.TimestampTz);
                writer.Write(r.Latitude, NpgsqlDbType.Double);
                writer.Write(r.Longitude, NpgsqlDbType.Double);
                WriteNullable(writer, r.SpeedOverGroundKn);
                WriteNullable(writer, r.CourseOverGround);
                WriteNullable(writer, r.HeadingDegrees);
                writer.Write(r.NavigationalStatus, NpgsqlDbType.Text);
                WriteNullable(writer, r.RateOfTurnDegPerMin);
                WriteNullable(writer, r.DraughtM);

                if (r.Destination is { } destination)
                {
                    writer.Write(destination, NpgsqlDbType.Text);
                }
                else
                {
                    writer.WriteNull();
                }

                if (r.EtaUtc is { } eta)
                {
                    writer.Write(Utc(eta), NpgsqlDbType.TimestampTz);
                }
                else
                {
                    writer.WriteNull();
                }

                writer.Write(accepted.QualityFlags, NpgsqlDbType.Text);
                writer.Write(r.SourceLine, NpgsqlDbType.Bigint);
                writer.Write((long)i, NpgsqlDbType.Bigint);
            }

            writer.Complete();
        }

        using var move = _connection.CreateCommand();
        move.Transaction = transaction;
        move.CommandText = """
            INSERT INTO position_report (mmsi, ts_utc, lat, lon, sog_kn, cog, heading,
                                         nav_status, rot, draught_m, destination, eta_utc,
                                         quality_flags, ingest_run_id, source_line)
            SELECT mmsi, ts_utc, lat, lon, sog_kn, cog, heading, nav_status, rot,
                   draught_m, destination, eta_utc, quality_flags,
                   $1, source_line
            FROM staging_position
            ORDER BY ord
            ON CONFLICT (mmsi, ts_utc, lat, lon) DO NOTHING;
            """;
        move.Parameters.AddWithValue(runId);
        var inserted = move.ExecuteNonQuery();

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
            INSERT INTO quarantine (rule_id, source_file, source_line, raw_snippet, detail,
                                    ingest_run_id)
            VALUES ($1, $2, $3, $4, $5, $6)
            ON CONFLICT (source_file, source_line, rule_id) DO NOTHING;
            """;
        var rule = command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text });
        var file = command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text });
        var line = command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint });
        var raw = command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text });
        var detail = command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text });
        var run = command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint });
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
        // carries none: static data rides only on message-type-5 rows (ADR-0007).
        // LEAST/GREATEST rather than SQLite's two-argument MIN/MAX -- same intent, different
        // spelling, and Postgres's MIN/MAX are aggregates that would not compile here.
        command.CommandText = """
            INSERT INTO vessel (mmsi, imo, name, callsign, ship_type, cargo_type,
                                position_fixing_device, length_m, width_m,
                                first_seen_utc, last_seen_utc)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)
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
              first_seen_utc = LEAST(excluded.first_seen_utc, vessel.first_seen_utc),
              last_seen_utc = GREATEST(excluded.last_seen_utc, vessel.last_seen_utc);
            """;
        var p = new NpgsqlParameter[11];
        for (var i = 0; i < p.Length; i++)
        {
            p[i] = command.Parameters.Add(new NpgsqlParameter());
        }

        foreach (var v in vessels)
        {
            p[0].Value = v.Mmsi;
            p[1].Value = (object?)v.Imo ?? DBNull.Value;
            p[2].Value = (object?)v.Name ?? DBNull.Value;
            p[3].Value = (object?)v.CallSign ?? DBNull.Value;
            p[4].Value = (object?)v.ShipType ?? DBNull.Value;
            p[5].Value = (object?)v.CargoType ?? DBNull.Value;
            p[6].Value = (object?)v.PositionFixingDevice ?? DBNull.Value;
            p[7].Value = (object?)v.LengthM ?? DBNull.Value;
            p[8].Value = (object?)v.WidthM ?? DBNull.Value;
            p[9].Value = Utc(v.FirstSeenUtc);
            p[10].Value = Utc(v.LastSeenUtc);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public IEnumerable<PositionFix> ReadFixesOrdered()
    {
        using var command = _connection.CreateCommand();

        // id is the tiebreak so the order is total. Two receivers reporting the same
        // vessel-second at different positions would otherwise sort arbitrarily, and a
        // non-deterministic read order makes detection produce different stops from identical
        // data -- the same reasoning as the SQLite adapter, and the same ORDER BY.
        command.CommandText = """
            SELECT id, mmsi, ts_utc, lat, lon, sog_kn, nav_status, quality_flags
            FROM position_report
            ORDER BY mmsi, ts_utc, id;
            """;

        // Postgres buffers the whole result set client-side unless told otherwise, which would
        // pull millions of rows into memory at once.
        using var reader = command.ExecuteReader(System.Data.CommandBehavior.SequentialAccess);
        while (reader.Read())
        {
            yield return new PositionFix
            {
                Id = reader.GetInt64(0),
                Mmsi = reader.GetInt64(1),
                TimestampUtc = reader.GetDateTime(2).ToUniversalTime(),
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

        // A separate connection: ReadFixesOrdered streams on the main one, and Npgsql allows
        // only one active command per connection. SQLite tolerates the interleaving; Postgres
        // does not, which is the kind of difference adapter parity exists to surface.
        using var connection = _source.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE position_report SET quality_flags = $1 WHERE id = $2;";
        var flags = command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text });
        var id = command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint });
        command.Prepare();

        foreach (var update in updates)
        {
            flags.Value = update.QualityFlags;
            id.Value = update.PositionId;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public long PruneBefore(DateTime cutoffUtc, long portCallsArchived, string archivePath)
    {
        using var connection = _source.OpenConnection();
        using var transaction = connection.BeginTransaction();

        // Every projection, then the old fixes. Dropping both keeps the invariant that everything
        // derived here comes from what is still here (ADR-0045).
        foreach (var table in new[] { "port_call_phase", "port_call", "stop_event" })
        {
            using var drop = connection.CreateCommand();
            drop.Transaction = transaction;
            drop.CommandText = $"DELETE FROM {table};";
            drop.ExecuteNonQuery();
        }

        using var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM position_report WHERE ts_utc < $1;";
        delete.Parameters.AddWithValue(Utc(cutoffUtc));
        var removed = (long)delete.ExecuteNonQuery();

        using var record = connection.CreateCommand();
        record.Transaction = transaction;
        record.CommandText = """
            INSERT INTO retention_event
              (applied_utc, cutoff_utc, fixes_removed, port_calls_archived, archive_path)
            VALUES ($1, $2, $3, $4, $5);
            """;
        record.Parameters.AddWithValue(Utc(DateTime.UtcNow));
        record.Parameters.AddWithValue(Utc(cutoffUtc));
        record.Parameters.AddWithValue(removed);
        record.Parameters.AddWithValue(portCallsArchived);
        record.Parameters.AddWithValue(archivePath);
        record.ExecuteNonQuery();

        transaction.Commit();
        return removed;
    }

    public void ReplaceDetections(IReadOnlyList<PortCall> portCalls)
    {
        using var connection = _source.OpenConnection();
        using var transaction = connection.BeginTransaction();

        // Phases first: they reference both tables below. Delete then rebuild inside one
        // transaction -- a partial replace would leave a mixture of two computations (ADR-0009).
        // Emptied, not dropped: keeping a projection's SHAPE current is EnsureSchema's job and
        // happens once, not on every run (ADR-0034).
        foreach (var table in new[] { "port_call_phase", "port_call", "stop_event" })
        {
            using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = $"DELETE FROM {table};";
            delete.ExecuteNonQuery();
        }

        foreach (var call in portCalls)
        {
            var callId = InsertPortCall(connection, transaction, call);

            foreach (var phase in call.Phases)
            {
                var stopId = InsertStop(connection, transaction, phase.Stop);
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO port_call_phase (port_call_id, stop_event_id, seq, phase)
                    VALUES ($1, $2, $3, $4);
                    """;
                command.Parameters.AddWithValue(callId);
                command.Parameters.AddWithValue(stopId);
                command.Parameters.AddWithValue(phase.Sequence);
                command.Parameters.AddWithValue(phase.Phase.ToString());
                command.ExecuteNonQuery();
            }
        }

        transaction.Commit();
    }

    private static long InsertPortCall(NpgsqlConnection c, NpgsqlTransaction t, PortCall call)
    {
        using var command = c.CreateCommand();
        command.Transaction = t;
        command.CommandText = """
            INSERT INTO port_call (mmsi, arrived_utc, departed_utc, waiting_hours, working_hours,
                                   unclassified_hours, centroid_lat, centroid_lon, is_complete,
                                   port_wpi_number, port_name, port_country, port_distance_nm)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13) RETURNING id;
            """;
        command.Parameters.AddWithValue(call.Mmsi);
        command.Parameters.AddWithValue(Utc(call.ArrivedUtc));
        command.Parameters.AddWithValue(Utc(call.DepartedUtc));
        command.Parameters.AddWithValue(call.WaitingHours);
        command.Parameters.AddWithValue(call.WorkingHours);
        command.Parameters.AddWithValue(call.UnclassifiedHours);
        command.Parameters.AddWithValue(call.CentroidLatitude);
        command.Parameters.AddWithValue(call.CentroidLongitude);
        command.Parameters.AddWithValue(call.IsComplete);
        command.Parameters.AddWithValue((object?)call.Attribution?.WpiNumber ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)call.Attribution?.Name ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)call.Attribution?.Country ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)call.Attribution?.DistanceNm ?? DBNull.Value);
        return (long)command.ExecuteScalar()!;
    }

    private static long InsertStop(NpgsqlConnection c, NpgsqlTransaction t, StopEvent stop)
    {
        using var command = c.CreateCommand();
        command.Transaction = t;
        command.CommandText = """
            INSERT INTO stop_event (mmsi, started_utc, ended_utc, duration_hours, centroid_lat,
                                    centroid_lon, max_drift_nm, fix_count, reliable_fix_count,
                                    geometry_trustworthy, reported_status, status_agrees,
                                    is_complete, first_position_id, last_position_id)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15) RETURNING id;
            """;
        command.Parameters.AddWithValue(stop.Mmsi);
        command.Parameters.AddWithValue(Utc(stop.StartedUtc));
        command.Parameters.AddWithValue(Utc(stop.EndedUtc));
        command.Parameters.AddWithValue(stop.DurationHours);
        command.Parameters.AddWithValue(stop.CentroidLatitude);
        command.Parameters.AddWithValue(stop.CentroidLongitude);
        command.Parameters.AddWithValue(stop.MaxDriftNm);
        command.Parameters.AddWithValue(stop.FixCount);
        command.Parameters.AddWithValue(stop.ReliableFixCount);
        command.Parameters.AddWithValue(stop.GeometryTrustworthy);
        command.Parameters.AddWithValue((object?)stop.ReportedStatus ?? DBNull.Value);
        command.Parameters.AddWithValue(stop.StatusAgrees);
        command.Parameters.AddWithValue(stop.IsComplete);
        command.Parameters.AddWithValue(stop.FirstPositionId);
        command.Parameters.AddWithValue(stop.LastPositionId);
        return (long)command.ExecuteScalar()!;
    }

    /// <summary>
    /// TIMESTAMPTZ requires a UTC DateTime. A Kind of Unspecified reaching here would be
    /// interpreted against the server's timezone, shifting every stored instant silently.
    /// </summary>
    private static DateTime Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    private static void WriteNullable(NpgsqlBinaryImporter writer, double? value)
    {
        if (value is { } v)
        {
            writer.Write(v, NpgsqlDbType.Double);
        }
        else
        {
            writer.WriteNull();
        }
    }

    private void Execute(string sql, NpgsqlTransaction? transaction = null)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _connection.Dispose();
        _source.Dispose();
    }
}
