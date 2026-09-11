using System.Globalization;
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

    public void EnsureSchema() => Execute(SqliteSchema.Ddl);

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
              (mmsi, ts_utc, lat, lon, sog_kn, cog, heading, nav_status,
               quality_flags, ingest_run_id, source_line)
            VALUES ($mmsi, $ts, $lat, $lon, $sog, $cog, $heading, $nav, $flags, $run, $line);
            """;

        var mmsi = command.Parameters.Add("$mmsi", SqliteType.Integer);
        var ts = command.Parameters.Add("$ts", SqliteType.Text);
        var lat = command.Parameters.Add("$lat", SqliteType.Real);
        var lon = command.Parameters.Add("$lon", SqliteType.Real);
        var sog = command.Parameters.Add("$sog", SqliteType.Real);
        var cog = command.Parameters.Add("$cog", SqliteType.Real);
        var heading = command.Parameters.Add("$heading", SqliteType.Real);
        var nav = command.Parameters.Add("$nav", SqliteType.Text);
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
            INSERT INTO vessel (mmsi, imo, name, callsign, ship_type, length_m, width_m,
                                first_seen_utc, last_seen_utc)
            VALUES ($mmsi, $imo, $name, $callsign, $type, $length, $width, $first, $last)
            ON CONFLICT (mmsi) DO UPDATE SET
              imo = COALESCE(excluded.imo, vessel.imo),
              name = COALESCE(excluded.name, vessel.name),
              callsign = COALESCE(excluded.callsign, vessel.callsign),
              ship_type = COALESCE(excluded.ship_type, vessel.ship_type),
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
            length.Value = (object?)vessel.LengthM ?? DBNull.Value;
            width.Value = (object?)vessel.WidthM ?? DBNull.Value;
            first.Value = Format(vessel.FirstSeenUtc);
            last.Value = Format(vessel.LastSeenUtc);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public void Dispose() => _connection.Dispose();

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
