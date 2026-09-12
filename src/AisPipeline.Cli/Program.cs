using System.Globalization;
using AisPipeline.Adapters.Csv;
using AisPipeline.Adapters.Postgres;
using AisPipeline.Adapters.Sof;
using AisPipeline.Adapters.Sqlite;
using AisPipeline.Adapters.Sql;
using AisPipeline.Core.Annotate;
using AisPipeline.Core.Domain;
using AisPipeline.Core.Laytime;
using AisPipeline.Core.Reconciliation;
using AisPipeline.Core.Sof;
using AisPipeline.Core.Query;
using AisPipeline.Core.Ports;
using AisPipeline.Core.Detection;
using AisPipeline.Core.Ingest;
using AisPipeline.Core.Quality;
using AisPipeline.Core.Quality.Rules;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Usage();
    return 0;
}

return args[0] switch
{
    "ingest" => Ingest(args[1..]),
    "detect" => Detect(args[1..]),
    "laytime" => Laytime(args[1..]),
    "reconcile" => Reconcile(args[1..]),
    "quality" => Quality(args[1..]),
    "stops" => Stops(args[1..]),
    "portcalls" => PortCalls(args[1..]),
    _ => Unknown(args[0]),
};

static int Unknown(string verb)
{
    Console.Error.WriteLine($"unknown verb '{verb}'");
    Usage();
    return 2;
}

static void Usage() => Console.WriteLine("""
    ais - AIS ingestion and analysis

      ais ingest <file.csv|file.zip> [--db <path> | --postgres <conn>] [--ship-type <type>] [--limit <n>]
      ais detect [--db <path> | --postgres <conn>]
      ais laytime --mmsi <n> [--allowed <h>] [--rate <perDay>] [--nor <iso>] [--turn <h>]
      ais reconcile --sof <file.json> [--allowed <h>] [--rate <perDay>] [--turn <h>]
      ais quality [--db <path> | --postgres <conn>]
      ais stops [--mmsi <n>] [--min-hours <h>] [--complete-only] [--disagreements] [--limit <n>]
      ais portcalls [--mmsi <n>] [--min-waiting-hours <h>] [--complete-only] [--limit <n>]

    Options:
      --db          SQLite file to read/write (default: data/ais.db)
      --postgres    Postgres connection string instead of SQLite. compose.yaml provides one:
                    Host=localhost;Port=55432;Database=ais;Username=ais;Password=ais
      --ship-type   keep only vessels whose resolved type matches, e.g. Tanker
      --limit       stop after N source lines; for the development loop only

    detect annotates the sequence rules and rebuilds stops and port calls. It is a full
    recompute: running it twice lands on exactly the same result.

    reconcile compares a Statement of Facts against what AIS observed for the same call, and
    prices the difference by running the laytime calculation on each timeline.

    quality reports every registered rule and what it did to the data: rows it rejected into
    quarantine, and rows it kept but flagged. A rule that never fired prints zero rather than
    vanishing -- silence and absence are different claims (ADR-0032).

    stops and portcalls list what detect derived, longest first -- not chronologically. A
    duration the pipeline will not stand behind prints as a lower bound with a leading >=, and
    a drift it will not stand behind prints as ?. Neither prints as a number (ADR-0011,
    ADR-0025). Pass --complete-only to get only rows whose extent is known.

    laytime computes a statement for a vessel's most recent complete port call. AIS supplies
    berthing and completion; Notice of Readiness comes from the charter party and defaults to
    arrival at the anchorage, which is an assumption the output states.
    """);

static int Ingest(string[] args)
{
    if (args.Length == 0)
    {
        Console.Error.WriteLine("ingest needs a source file");
        return 2;
    }

    var source = args[0];
    if (!File.Exists(source))
    {
        Console.Error.WriteLine($"no such file: {source}");
        return 2;
    }

    var shipType = ValueOf(args, "--ship-type");
    long? limit = null;
    if (ValueOf(args, "--limit") is { } raw)
    {
        // TryParse, not Parse: a malformed value is user error and belongs on the same
        // graceful path as a missing file, not an unhandled FormatException and a stack trace.
        if (!long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            || parsed <= 0)
        {
            Console.Error.WriteLine($"--limit needs a positive whole number, got '{raw}'");
            return 2;
        }

        limit = parsed;
    }

    using var store = OpenStore(args);
    var pipeline = new IngestPipeline(
        store,
        RuleRegistry.Default(),
        new IngestOptions { ShipType = shipType, Limit = limit });

    var startedUtc = DateTime.UtcNow;
    var result = pipeline.Run(new DmaCsvSource(source));
    var elapsed = DateTime.UtcNow - startedUtc;

    Console.WriteLine($"run {result.RunId}  {Path.GetFileName(source)}  ({elapsed.TotalSeconds:F1}s)");
    Console.WriteLine($"  {result.Counters}");
    Console.WriteLine($"  vessels in scope: {result.VesselsInScope:N0}");

    if (result.FeedRuleHits.Count > 0)
    {
        Console.WriteLine("  rule hits across the whole feed:");
        foreach (var (ruleId, count) in result.FeedRuleHits.OrderBy(h => h.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"    {ruleId,-4} {count,12:N0}");
        }
    }

    // Every row read must land in exactly one bucket. If it does not, rows went missing
    // without a record -- blocking class 1 -- and the run must not be reported as a success.
    if (!result.Counters.IsBalanced)
    {
        Console.Error.WriteLine(
            $"  ACCOUNTING ERROR: {result.Counters.RowsRead:N0} rows read but " +
            $"{result.Counters.Accounted:N0} accounted for");
        return 1;
    }

    return 0;
}

static int Detect(string[] args)
{
    if (ValueOf(args, "--postgres") is null)
    {
        var database = ValueOf(args, "--db") ?? Path.Combine("data", "ais.db");
        if (!File.Exists(database))
        {
            Console.Error.WriteLine($"no database at {database}; run ingest first");
            return 2;
        }
    }

    using var store = OpenStore(args);
    store.EnsureSchema();

    // The annotate pass must finish before detection: detection excludes flagged fixes from
    // centroid and drift, so the other order would let a corrupt position into the geometry
    // (ADR-0021).
    var startedUtc = DateTime.UtcNow;
    // The registry's list, not a list written out here: the rules the report enumerates and the
    // rules this pass applies have to be the same object, or the report tells the truth about a
    // check nobody performed (ADR-0032).
    var annotated = new AnnotatePass(store, RuleRegistry.Default().SequenceRules).Run();

    Console.WriteLine($"annotated {annotated.FixesExamined:N0} fixes");
    foreach (var (ruleId, count) in annotated.RuleHits.OrderBy(h => h.Key, StringComparer.Ordinal))
    {
        Console.WriteLine($"    {ruleId,-4} {count,12:N0}");
    }

    var detected = new DetectionPass(store).Run();
    var elapsed = DateTime.UtcNow - startedUtc;

    Console.WriteLine($"detected across {detected.VesselsExamined:N0} vessels ({elapsed.TotalSeconds:F1}s)");
    Console.WriteLine($"  stops {detected.StopsDetected:N0}  " +
        $"(complete {detected.CompleteStops:N0})  port calls {detected.PortCalls:N0}");

    if (detected.StopsDetected > 0)
    {
        var share = 100.0 * detected.StopsWhereStatusDisagrees / detected.StopsDetected;
        Console.WriteLine(
            $"  stops where the vessel's own status contradicted its speed: " +
            $"{detected.StopsWhereStatusDisagrees:N0} ({share:F1}%)");
    }

    return 0;
}

static int Laytime(string[] args)
{
    if (ValueOf(args, "--mmsi") is not { } rawMmsi
        || !long.TryParse(rawMmsi, NumberStyles.None, CultureInfo.InvariantCulture, out var mmsi))
    {
        Console.Error.WriteLine("laytime needs --mmsi <9 digits>");
        return 2;
    }

    var connection = ReadConnection.Resolve(ValueOf(args, "--postgres"), ValueOf(args, "--db"));

    if (connection.Dialect == SqlDialect.Sqlite && !File.Exists(connection.SqlitePath))
    {
        // Checked rather than caught. The store opens read-only, so a mistyped path throws
        // "unable to open database file" -- accurate, but a stack trace is not an error message,
        // and this is the same graceful path every other malformed input on this CLI takes.
        Console.Error.WriteLine($"no database at {connection.SqlitePath}; run ingest and detect first");
        return 2;
    }

    using var queries = connection.OpenQueries();

    var call = queries.MostRecentCompletePortCall(mmsi);

    if (call is null)
    {
        Console.Error.WriteLine($"no complete port call for mmsi {mmsi}; run detect first");
        return 2;
    }

    var phases = queries.GetPhasesForPortCalls([call.Id]);

    // Rebuild the domain shape and let VoyageTimeline decide what can be measured, rather than
    // picking berth timestamps out of the rows here. It refuses to fabricate a berth time from an
    // anchorage-only call, and it reports hours inside the berth span whose geometry the pipeline
    // does not stand behind.
    var portCall = new PortCall
    {
        Mmsi = call.Mmsi,
        Phases = [.. phases
            .OrderBy(p => p.Phase.Sequence)
            .Select(p => new PortCallPhase(
                p.Phase.Sequence,
                Enum.Parse<StopPhase>(p.Phase.Phase),
                new StopEvent
                {
                    Mmsi = p.Stop.Mmsi,
                    StartedUtc = p.Stop.StartedUtc,
                    EndedUtc = p.Stop.EndedUtc,
                    CentroidLatitude = p.Stop.CentroidLatitude,
                    CentroidLongitude = p.Stop.CentroidLongitude,
                    MaxDriftNm = p.Stop.ObservedMaxDriftNm,
                    FixCount = p.Stop.FixCount,
                    ReliableFixCount = p.Stop.ReliableFixCount,
                    ReportedStatus = p.Stop.ReportedStatus,
                    StatusAgrees = p.Stop.StatusAgrees,
                    IsComplete = p.Stop.IsComplete,
                    FirstPositionId = p.Stop.FirstPositionId,
                    LastPositionId = p.Stop.LastPositionId,
                }))],
    };

    if (VoyageTimeline.FromPortCall(portCall) is not { } timeline)
    {
        Console.Error.WriteLine(
            $"port call {call.Id} has no berth phase, so there are no cargo operations to measure");
        return 2;
    }

    if (!timeline.BerthSpanIsTrustworthy)
    {
        // Neither excluding these hours (which favours the charterer) nor counting them (which
        // favours the owner) is supportable, so no figure is produced. ADR-0025's own title:
        // a stop must refuse to guess.
        Console.Error.WriteLine(
            $"port call {call.Id}: {timeline.UntrustworthyHoursInBerthSpan:F2}h inside the berth " +
            "span have geometry the pipeline does not stand behind, so this call cannot be priced. " +
            "Re-run detect after ingesting more of the window, or price it by hand.");
        return 2;
    }

    var berthedUtc = timeline.BerthedUtc!.Value;
    var completedUtc = timeline.DepartedBerthUtc!.Value;

    // NOR is a document, not a physical event -- no transponder emits one. Substituting arrival
    // is an assumption, and it travels with the terms into the statement rather than being
    // annotated once at the point of printing.
    var norAssumed = ValueOf(args, "--nor") is null;
    var nor = norAssumed
        ? call.ArrivedUtc
        : DateTime.Parse(ValueOf(args, "--nor")!, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    var terms = new CharterPartyTerms
    {
        LaytimeAllowedHours = Number(args, "--allowed", 72.0),
        DemurrageRatePerDay = Money.FromMajor((decimal)Number(args, "--rate", 28_000.0), "USD"),
        NoticeOfReadinessUtc = nor,
        NoticeOfReadinessIsAssumed = norAssumed,
        TurnTimeHours = Number(args, "--turn", 6.0),
    };

    var statement = new LaytimeCalculator().Calculate(terms, berthedUtc, completedUtc);

    Console.WriteLine($"mmsi {mmsi}  port call {call.Id}  {call.ArrivedUtc:yyyy-MM-dd HH:mm} -> {call.DepartedUtc:yyyy-MM-dd HH:mm}");
    Console.WriteLine($"  from AIS:  waiting {call.WaitingHours:F1}h   working {call.WorkingHours:F1}h");
    Console.WriteLine($"  NOR:       {nor:yyyy-MM-dd HH:mm}{(norAssumed ? "  (ASSUMED = arrival; AIS cannot observe a notice)" : "  (given)")}");
    Console.WriteLine();
    Console.WriteLine(statement);
    return 0;
}

static int Reconcile(string[] args)
{
    if (ValueOf(args, "--sof") is not { } sofPath)
    {
        Console.Error.WriteLine("reconcile needs --sof <file.json>");
        return 2;
    }

    if (!File.Exists(sofPath))
    {
        Console.Error.WriteLine($"no statement of facts at {sofPath}");
        return 2;
    }

    StatementOfFacts sof;
    try
    {
        sof = SofJsonReader.ReadFile(sofPath);
    }
    catch (InvalidDataException e)
    {
        // A malformed document is user error, not a crash. The message names the line.
        Console.Error.WriteLine(e.Message);
        return 2;
    }

    var connection = ReadConnection.Resolve(ValueOf(args, "--postgres"), ValueOf(args, "--db"));

    if (connection.Dialect == SqlDialect.Sqlite && !File.Exists(connection.SqlitePath))
    {
        Console.Error.WriteLine($"no database at {connection.SqlitePath}; run ingest and detect first");
        return 2;
    }

    using var queries = connection.OpenQueries();

    if (queries.MostRecentCompletePortCall(sof.Mmsi) is not { } call)
    {
        Console.Error.WriteLine(
            $"no complete port call for mmsi {sof.Mmsi}; this document cannot be matched to AIS");
        return 2;
    }

    var portCall = RebuildPortCall(call, queries.GetPhasesForPortCalls([call.Id]));

    var terms = new CharterPartyTerms
    {
        LaytimeAllowedHours = Number(args, "--allowed", 72.0),
        DemurrageRatePerDay = Money.FromMajor((decimal)Number(args, "--rate", 28_000.0), "USD"),
        // The document supplies the notice, so nothing is assumed here -- unlike `laytime`,
        // which has to substitute arrival when no charter party is to hand.
        NoticeOfReadinessUtc = sof.First(SofEventKind.NoticeOfReadinessTendered)?.TimestampUtc
            ?? call.ArrivedUtc,
        NoticeOfReadinessIsAssumed = sof.First(SofEventKind.NoticeOfReadinessTendered) is null,
        TurnTimeHours = Number(args, "--turn", 6.0),
    };

    Reconciliation result;
    try
    {
        result = new TimelineReconciler().Reconcile(sof, portCall, terms);
    }
    catch (ArgumentException e)
    {
        Console.Error.WriteLine($"cannot reconcile: {e.Message}");
        return 2;
    }

    Console.WriteLine($"{sof.VesselName} (mmsi {sof.Mmsi})  {sof.Port}");
    Console.WriteLine($"  statement : {Path.GetFileName(sofPath)}  ({sof.Events.Count} events)");

    if (!string.IsNullOrWhiteSpace(sof.PreparedBy))
    {
        // Who wrote the document belongs beside the figure derived from it -- not least because
        // the committed fixture says "constructed fixture", and output that looks like a real
        // reconciliation should say when it is not one.
        Console.WriteLine($"  prepared  : {sof.PreparedBy}");
    }
    Console.WriteLine($"  AIS call  : {call.Id}  {call.ArrivedUtc:yyyy-MM-dd HH:mm} -> {call.DepartedUtc:yyyy-MM-dd HH:mm}");
    Console.WriteLine();
    Console.WriteLine(result);

    if (result.ClassifiedButNotCompared.Count > 0)
    {
        // Recognised duplicates. Previously invisible: absent from the table because an earlier
        // event of the same kind was used, and absent from the unclassified list because they
        // were classified.
        Console.WriteLine();
        Console.WriteLine(
            $"  {result.ClassifiedButNotCompared.Count} further event(s) of a compared kind, not used:");
        foreach (var e in result.ClassifiedButNotCompared)
        {
            Console.WriteLine($"    {e.TimestampUtc:yyyy-MM-dd HH:mm}  {e.Kind,-26} {e.Label}");
        }
    }

    var unrecognised = sof.Events.Count(e => e.Kind == SofEventKind.Other);
    if (unrecognised > 0)
    {
        // Kept, not dropped -- and said out loud, because a line nobody classified is a line
        // nobody compared.
        Console.WriteLine();
        Console.WriteLine($"  {unrecognised} event(s) kept but not classified, so not compared:");
        foreach (var e in sof.Events.Where(e => e.Kind == SofEventKind.Other))
        {
            Console.WriteLine($"    {e.TimestampUtc:yyyy-MM-dd HH:mm}  {e.Label}");
        }
    }

    return 0;
}

static int Quality(string[] args)
{
    var connection = ReadConnection.Resolve(ValueOf(args, "--postgres"), ValueOf(args, "--db"));

    if (connection.Dialect == SqlDialect.Sqlite && !File.Exists(connection.SqlitePath))
    {
        Console.Error.WriteLine($"no database at {connection.SqlitePath}; run ingest first");
        return 2;
    }

    using var queries = connection.OpenQueries();

    var report = QualityReport.Build(RuleRegistry.Default(), queries.QualityReport());
    var runs = queries.ListRuns();

    Console.WriteLine($"{runs.Count} ingest run(s):");
    foreach (var run in runs)
    {
        Console.WriteLine(
            $"  run {run.Id,-4} {Path.GetFileName(run.SourceFile),-32} " +
            $"read {run.RowsRead,12:N0}  inserted {run.RowsInserted,12:N0}  " +
            $"quarantined {run.RowsQuarantined,10:N0}");
    }

    Console.WriteLine();

    // Said before the table, because the number above it is bigger. `ais ingest` counts hits per
    // line READ; this counts what the store HOLDS, and the fixture's 26 R5 hits become 10 flagged
    // rows once duplicates within the file collapse onto one natural key. Two honest numbers that
    // disagree is the project's recurring shape -- so name which one this is (rule 4).
    Console.WriteLine("  Counts below are over what the store holds, not over lines read: a rule");
    Console.WriteLine("  that fired on a duplicate leaves one record, not one per occurrence.");
    Console.WriteLine();
    Console.WriteLine($"  {"rule",-5} {"rejected",12} {"flagged",12}  what it catches");

    foreach (var line in report)
    {
        var rejected = line.Quarantined.ToString("N0", CultureInfo.InvariantCulture);
        var flagged = line.Flagged.ToString("N0", CultureInfo.InvariantCulture);
        Console.WriteLine($"  {line.RuleId,-5} {rejected,12} {flagged,12}  {line.Description}");
    }

    var silent = report.Where(l => l.Silent).Select(l => l.RuleId).ToList();
    if (silent.Count > 0)
    {
        // Named rather than left as a row of zeroes to scan for. A rule that has never fired is
        // either a check the feed does not need or a check that is broken, and the reader cannot
        // tell which without being told the rule ran at all.
        Console.WriteLine();
        Console.WriteLine(
            $"  {string.Join(", ", silent)} ran and never fired on this data.");
    }

    Console.WriteLine();
    Console.WriteLine(
        $"  totals: {report.Sum(l => l.Quarantined):N0} rejected into quarantine, " +
        $"{report.Sum(l => l.Flagged):N0} kept with a flag.");
    Console.WriteLine(
        "  Rejected rows are not in the time series; flagged rows are, carrying their doubt.");

    return 0;
}


/// <summary>
/// Hours, or the lower bound when the figure is censored.
///
/// A stop touching a coverage gap or the edge of the window has an unknown true duration
/// (ADR-0011), and the read model makes that a null rather than a number. Printing the observed
/// span bare would launder a lower bound into a measurement; printing nothing would throw away a
/// fact that is available and useful. So it prints with a >=, which is exactly what is known.
/// </summary>
static string Hours(double? figure, double observed) =>
    figure is { } known
        ? known.ToString("F1", CultureInfo.InvariantCulture).PadLeft(8)
        : (">=" + observed.ToString("F1", CultureInfo.InvariantCulture)).PadLeft(8);

/// <summary>Drift, or ? when too few fixes survived exclusion for it to mean anything (ADR-0025).</summary>
static string Drift(double? nm) =>
    nm is { } known ? known.ToString("F3", CultureInfo.InvariantCulture).PadLeft(7) : "?".PadLeft(7);

/// <summary>
/// Says so when the page is full.
///
/// A list returned at exactly its limit is indistinguishable from a complete one, and a reader
/// totalling what they can see would be short by an unknown amount. The same defect ADR-0028
/// found in the GraphQL port-call cap, and the fix is the same: make the truncation detectable.
/// </summary>
static void NoteIfCapped(int returned, int limit)
{
    if (returned == limit)
    {
        Console.WriteLine();
        Console.WriteLine(
            $"  exactly {limit} row(s) returned, which is the limit -- there are probably more. " +
            "Raise --limit or narrow the filters.");
    }
}

static int Stops(string[] args)
{
    if (Filters(args, out var connection, out var limit) is { } failure)
    {
        return failure;
    }

    using var queries = connection.OpenQueries();

    var stops = queries.ListStops(new StopFilter
    {
        Mmsi = OptionalMmsi(args),
        MinHours = ValueOf(args, "--min-hours") is not null ? Number(args, "--min-hours", 0.0) : null,
        CompleteOnly = Present(args, "--complete-only"),
        DisagreementsOnly = Present(args, "--disagreements"),
        Limit = limit,
    });

    if (stops.Count == 0)
    {
        Console.WriteLine("no stops match. Run detect first, or loosen the filters.");
        return 0;
    }

    Console.WriteLine($"{stops.Count} stop(s), longest first:");
    Console.WriteLine();
    Console.WriteLine("  mmsi       started (UTC)     hours    drift nm  status");

    foreach (var stop in stops)
    {
        // The vessel's own status and what it does with the contradiction, both on the line. Rule
        // 4: the disagreement is stored and shown, never resolved into a winner.
        var status = stop.ReportedStatus ?? "(none reported)";
        var verdict = stop.StatusAgrees ? "" : "  CONTRADICTS its own speed";

        Console.WriteLine(
            $"  {stop.Mmsi,-10} {stop.StartedUtc:yyyy-MM-dd HH:mm}  " +
            $"{Hours(stop.DurationHours, stop.ObservedDurationHours)}  {Drift(stop.MaxDriftNm)}  " +
            $"{status}{verdict}");
    }

    var open = stops.Count(s => !s.IsComplete);
    if (open > 0)
    {
        Console.WriteLine();
        Console.WriteLine(
            $"  {open} of these have an unknown true extent -- they touch a coverage gap or the " +
            "edge of the ingested window, so their hours are lower bounds (ADR-0011).");
    }

    NoteIfCapped(stops.Count, limit);
    return 0;
}

static int PortCalls(string[] args)
{
    if (Filters(args, out var connection, out var limit) is { } failure)
    {
        return failure;
    }

    using var queries = connection.OpenQueries();

    var calls = queries.ListPortCalls(new PortCallFilter
    {
        Mmsi = OptionalMmsi(args),
        MinWaitingHours = ValueOf(args, "--min-waiting-hours") is not null
            ? Number(args, "--min-waiting-hours", 0.0)
            : null,
        CompleteOnly = Present(args, "--complete-only"),
        Limit = limit,
    });

    if (calls.Count == 0)
    {
        Console.WriteLine("no port calls match. Run detect first, or loosen the filters.");
        return 0;
    }

    Console.WriteLine($"{calls.Count} port call(s), longest first:");
    Console.WriteLine();
    Console.WriteLine("  id     mmsi       arrived (UTC)      waiting  working  unclassified");

    foreach (var call in calls)
    {
        Console.WriteLine(
            $"  {call.Id,-6} {call.Mmsi,-10} {call.ArrivedUtc:yyyy-MM-dd HH:mm}  " +
            $"{call.WaitingHours,9:F1}{call.WorkingHours,9:F1}" +
            $"{call.UnclassifiedHours,14:F1}{(call.IsComplete ? "" : "  (open)")}");
    }

    var unclassified = calls.Sum(c => c.UnclassifiedHours);
    if (unclassified > 0)
    {
        // Neither waiting nor working. Counting these as either would favour one party to a
        // charter over the other, so they are carried as their own column (ADR-0025).
        Console.WriteLine();
        Console.WriteLine(
            $"  {unclassified:F1}h across these calls sit in phases whose geometry the pipeline " +
            "does not stand behind: neither waiting nor working, and not billable as either.");
    }

    var open = calls.Count(c => !c.IsComplete);
    if (open > 0)
    {
        Console.WriteLine();
        Console.WriteLine(
            $"  {open} marked (open): the call's true extent is unknown, so its hours are lower " +
            "bounds. --complete-only excludes them.");
    }

    NoteIfCapped(calls.Count, limit);
    return 0;
}

/// <summary>
/// The setup both read verbs share: resolve the engine, check it is there, read --limit.
/// Returns an exit code when something is wrong, null when the caller may proceed.
/// </summary>
static int? Filters(string[] args, out ReadConnection connection, out int limit)
{
    connection = ReadConnection.Resolve(ValueOf(args, "--postgres"), ValueOf(args, "--db"));
    limit = 100;

    if (connection.Dialect == SqlDialect.Sqlite && !File.Exists(connection.SqlitePath))
    {
        Console.Error.WriteLine($"no database at {connection.SqlitePath}; run ingest and detect first");
        return 2;
    }

    if (ValueOf(args, "--limit") is { } raw)
    {
        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            || parsed <= 0)
        {
            Console.Error.WriteLine($"--limit needs a positive whole number, got '{raw}'");
            return 2;
        }

        limit = parsed;
    }

    return null;
}

/// <summary>--mmsi if given and well formed, null if absent. A malformed value is not silently all.</summary>
static long? OptionalMmsi(string[] args) =>
    ValueOf(args, "--mmsi") is { } raw
     && long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var mmsi)
        ? mmsi
        : null;

static bool Present(string[] args, string name) => Array.IndexOf(args, name) >= 0;

/// <summary>Rebuilds the domain shape from stored rows, so detection logic is not duplicated here.</summary>
static PortCall RebuildPortCall(
    AisPipeline.Core.Query.StoredPortCall call,
    IReadOnlyList<(AisPipeline.Core.Query.StoredPhase Phase, AisPipeline.Core.Query.StoredStop Stop)> phases) => new()
    {
        Mmsi = call.Mmsi,
        Phases = [.. phases
        .OrderBy(p => p.Phase.Sequence)
        .Select(p => new PortCallPhase(
            p.Phase.Sequence,
            Enum.Parse<StopPhase>(p.Phase.Phase),
            new StopEvent
            {
                Mmsi = p.Stop.Mmsi,
                StartedUtc = p.Stop.StartedUtc,
                EndedUtc = p.Stop.EndedUtc,
                CentroidLatitude = p.Stop.CentroidLatitude,
                CentroidLongitude = p.Stop.CentroidLongitude,
                MaxDriftNm = p.Stop.ObservedMaxDriftNm,
                FixCount = p.Stop.FixCount,
                ReliableFixCount = p.Stop.ReliableFixCount,
                ReportedStatus = p.Stop.ReportedStatus,
                StatusAgrees = p.Stop.StatusAgrees,
                IsComplete = p.Stop.IsComplete,
                FirstPositionId = p.Stop.FirstPositionId,
                LastPositionId = p.Stop.LastPositionId,
            }))],
    };

static double Number(string[] args, string name, double fallback) =>
    ValueOf(args, name) is { } raw
     && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
        ? parsed
        : fallback;

/// <summary>
/// Pick the adapter. The pipeline does not know or care which one it got -- that is the whole
/// point of the ports, and the integration suite runs the same assertions against both
/// (ADR-0026).
/// </summary>
static IAisStore OpenStore(string[] args)
{
    if (ValueOf(args, "--postgres") is { } connectionString)
    {
        return new PostgresAisStore(connectionString);
    }

    var database = ValueOf(args, "--db") ?? Path.Combine("data", "ais.db");
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(database))!);
    return new SqliteAisStore(database);
}

static string? ValueOf(string[] args, string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
