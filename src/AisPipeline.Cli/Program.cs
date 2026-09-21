using AisPipeline.Core.Benchmarks;
using System.Globalization;
using AisPipeline.Adapters.Archive;
using AisPipeline.Adapters.Csv;
using AisPipeline.Adapters.Postgres;
using AisPipeline.Adapters.Sof;
using AisPipeline.Adapters.Sqlite;
using AisPipeline.Adapters.Sql;
using AisPipeline.Core.Annotate;
using AisPipeline.Core.Archive;
using AisPipeline.Core.Retention;
using AisPipeline.Core.Domain;
using AisPipeline.Core.Laytime;
using AisPipeline.Core.Reconciliation;
using AisPipeline.Core.Sof;
using AisPipeline.Core.Query;
using AisPipeline.Core.Ports;
using System.Text.Json;
using AisPipeline.Core.Detection;
using AisPipeline.Core.Export;
using AisPipeline.Core.Geo;
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
    "export" => Export(args[1..]),
    "prune" => Prune(args[1..]),
    "archive" => ReadArchive(args[1..]),
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
      ais detect [--db <path> | --postgres <conn>] [--ports <gazetteer.csv>]
      ais laytime --mmsi <n> [--allowed <h>] [--rate <perDay>] [--nor <iso>] [--turn <h>]
      ais reconcile --sof <file.json> [--allowed <h>] [--rate <perDay>] [--turn <h>]
      ais quality [--db <path> | --postgres <conn>]
      ais export --out <dir> [--db <path> | --postgres <conn>]
      ais prune --keep-days <n> [--archive <dir>] [--force] [--db <path> | --postgres <conn>]
      ais archive <file.json> [--mmsi <n>]
      ais stops [--mmsi <n>] [--min-hours <h>] [--complete-only] [--disagreements] [--limit <n>]
      ais portcalls [--mmsi <n>] [--min-waiting-hours <h>] [--complete-only] [--limit <n>]

    Options:
      --db          SQLite file to read/write (default: data/ais.db)
      --postgres    Postgres connection string instead of SQLite. compose.yaml provides one:
                    Host=localhost;Port=55432;Database=ais;Username=ais;Password=ais
      --ship-type   keep only vessels whose resolved type matches, e.g. Tanker
      --limit       stop after N source lines; for the development loop only
      --ports       port gazetteer to name stops against
                    (default: reference/ports/wpi-baltic-north-sea.csv)

    detect annotates the sequence rules and rebuilds stops and port calls. It is a full
    recompute: running it twice lands on exactly the same result.

    reconcile compares a Statement of Facts against what AIS observed for the same call, and
    prices the difference by running the laytime calculation on each timeline.

    quality reports every registered rule and what it did to the data: rows it rejected into
    quarantine, and rows it kept but flagged. A rule that never fired prints zero rather than
    vanishing -- silence and absence are different claims (ADR-0032).

    prune archives the port calls it is about to lose, then removes every projection and every
    fix older than --keep-days. Projections go too, on purpose: they are a total function of the
    log, and a derived layer over a partly-pruned log is a contradiction that produces a defect
    at every seam. Run detect afterwards to rebuild from what remains (ADR-0045).

    archive reads back what prune wrote. Past the cutoff that file is the only record a port
    call ever happened -- the fixes it was derived from are gone and rule 5 means nothing can
    rebuild it -- so it is the one document here that has to be readable years after the code
    that wrote it. This verb is what proves it still is.

    export writes the derived layer as JSON for a static site to build from. It carries its own
    provenance -- which source files, which window, how many rows -- so a published figure can say
    where it came from (ADR-0039).

    stops and portcalls list what detect derived, longest first -- not chronologically. A figure
    the pipeline will not stand behind prints as a bound (>=2.7) or as ?, never as a number.

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

    var gazetteerPath = ValueOf(args, "--ports") ?? WpiCsvGazetteer.DefaultPath;
    NearestPortIndex? ports = null;

    if (File.Exists(gazetteerPath))
    {
        ports = new NearestPortIndex(WpiCsvGazetteer.Load(gazetteerPath));
        Console.WriteLine($"gazetteer: {ports.Count} ports from {gazetteerPath}");
    }
    else
    {
        // Said, not skipped. Without this line a reader would see port columns full of nulls and
        // conclude the vessels were all at sea.
        Console.Error.WriteLine(
            $"no gazetteer at {gazetteerPath}; port calls will carry no port name. " +
            "Pass --ports <file> or restore reference/ports/.");
    }

    var detected = new DetectionPass(store, ports: ports).Run();
    var elapsed = DateTime.UtcNow - startedUtc;

    Console.WriteLine($"detected across {detected.VesselsExamined:N0} vessels ({elapsed.TotalSeconds:F1}s)");
    Console.WriteLine($"  stops {detected.StopsDetected:N0}  " +
        $"(complete {detected.CompleteStops:N0})  port calls {detected.PortCalls:N0}");

    if (detected.PortCalls > 0 && ports is not null)
    {
        var named = 100.0 * detected.PortCallsNamed / detected.PortCalls;
        var atPort = 100.0 * detected.PortCallsPlausiblyAtPort / detected.PortCalls;
        Console.WriteLine(
            $"  named a port for {detected.PortCallsNamed:N0} of them ({named:F0}%), " +
            $"of which {detected.PortCallsPlausiblyAtPort:N0} ({atPort:F0}%) sit within " +
            $"{PortAttributionThresholds.PlausiblyAtPortNm:F0} nm of it -- a heuristic (ADR-0034)");
    }

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

    // The rebuild and the assessment are shared with the API, so the two cannot disagree about
    // when a call is priceable (ADR-0042).
    var portCall = PortCallRebuilder.FromStored(call, queries.GetPhasesForPortCalls([call.Id]));

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
        LaytimeAllowedHours = Number(args, "--allowed", CharterPartyDefaults.AllowedHours),
        DemurrageRatePerDay = Money.FromMajor(
            (decimal)Number(args, "--rate", CharterPartyDefaults.RatePerDay),
            CharterPartyDefaults.Currency),
        NoticeOfReadinessUtc = nor,
        NoticeOfReadinessIsAssumed = norAssumed,
        TurnTimeHours = Number(args, "--turn", CharterPartyDefaults.TurnHours),
    };

    var assessment = LaytimeAssessor.Assess(portCall, terms);

    if (!assessment.IsPriced)
    {
        // Neither excluding untrustworthy hours (which favours the charterer) nor counting them
        // (which favours the owner) is supportable, so no figure is produced at all.
        Console.Error.WriteLine($"port call {call.Id}: {assessment.RefusalDetail}.");
        Console.Error.WriteLine(
            "  Re-run detect after ingesting more of the window, or price it by hand.");
        return 2;
    }

    var statement = assessment.Statement!;

    Console.WriteLine($"mmsi {mmsi}  port call {call.Id}  {call.ArrivedUtc:yyyy-MM-dd HH:mm} -> {call.DepartedUtc:yyyy-MM-dd HH:mm}");

    // The statement below carries a money figure, so it should say where the vessel was. With the
    // distance, and marked when it is only the nearest port rather than plausibly the right one --
    // a laytime statement for "somewhere within 14 nm of Kalundborg" should look like one.
    Console.WriteLine(call.PortName is null
        ? "  port:      no port named for this call (none within range, or detect ran without a gazetteer)"
        : $"  port:      {call.PortName}, {call.PortCountry} " +
          $"({call.PortDistanceNm!.Value.ToString("F1", CultureInfo.InvariantCulture)} nm" +
          $"{(call.PlausiblyAtPort == true ? "" : "; NEAREST only, too far to call the vessel alongside it")})");
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

    if (sof.Events.Count == 0)
    {
        Console.Error.WriteLine($"{Path.GetFileName(sofPath)} has no events, so it describes no call");
        return 2;
    }

    // The document's own span selects the call. Asking for the vessel's most recent complete call
    // and then validating that guess refused every historical document, which is most of them
    // (ADR-0035).
    var documentFrom = sof.Events.Min(e => e.TimestampUtc);
    var documentTo = sof.Events.Max(e => e.TimestampUtc);

    var overlapping = queries.PortCallsOverlapping(sof.Mmsi, documentFrom, documentTo);

    var selection = CallSelection.Select(
        documentFrom,
        documentTo,
        [.. overlapping.Select(c => new CallCandidate(c.Id, c.ArrivedUtc, c.DepartedUtc))]);

    if (selection.NothingOverlaps)
    {
        Console.Error.WriteLine(
            $"no AIS port call for mmsi {sof.Mmsi} shares any time with " +
            $"{documentFrom:yyyy-MM-dd HH:mm} to {documentTo:yyyy-MM-dd HH:mm}.");

        // What the store DOES hold for this vessel, so the reader can tell "wrong window" from
        // "vessel never detected" without running a second verb.
        var held = queries.ListPortCalls(new PortCallFilter { Mmsi = sof.Mmsi, Limit = 3 });
        if (held.Count == 0)
        {
            Console.Error.WriteLine(
                "  This vessel has no detected port calls at all. Ingest the window the document " +
                "covers, then run detect.");
        }
        else
        {
            Console.Error.WriteLine($"  It does have {held.Count} other call(s), for example:");
            foreach (var other in held)
            {
                Console.Error.WriteLine(
                    $"    call {other.Id}  {other.ArrivedUtc:yyyy-MM-dd HH:mm} -> " +
                    $"{other.DepartedUtc:yyyy-MM-dd HH:mm}");
            }
        }

        return 2;
    }

    if (selection.IsAmbiguous)
    {
        // Two calls sharing exactly as much time with the document. Breaking the tie by recency or
        // id would silently decide which timeline the money is measured against (ADR-0035).
        Console.Error.WriteLine(
            $"{selection.Ranked.Count} AIS port calls share the same amount of time with this " +
            "document, so which one it describes cannot be decided from timestamps:");
        foreach (var (candidate, overlap) in selection.Ranked)
        {
            Console.Error.WriteLine(
                $"    call {candidate.Id}  {candidate.ArrivedUtc:yyyy-MM-dd HH:mm} -> " +
                $"{candidate.DepartedUtc:yyyy-MM-dd HH:mm}  overlap {overlap.TotalHours:F1}h");
        }

        Console.Error.WriteLine("  Nothing is reconciled.");
        return 2;
    }

    // Taken from the rows the overlap query already returned, not re-fetched through
    // ListPortCalls: that one ranks by total duration before applying its limit, so the chosen
    // call can be absent from the page entirely (ADR-0028).
    var call = overlapping.Single(c => c.Id == selection.Chosen!.Id);

    var portCall = PortCallRebuilder.FromStored(call, queries.GetPhasesForPortCalls([call.Id]));

    // Still evaluated, and still the reconciler's own precondition. The selector picked on overlap
    // so this cannot fail today -- it is the invariant that keeps the two agreeing if either
    // changes.
    var match = CallMatch.Evaluate(sof, portCall);
    if (!match.TimesAreConsistent)
    {
        Console.Error.WriteLine(
            $"the statement and AIS port call {call.Id} do not describe the same visit:");
        Console.Error.WriteLine($"  {match.Explain()}");
        return 2;
    }

    var terms = new CharterPartyTerms
    {
        LaytimeAllowedHours = Number(args, "--allowed", CharterPartyDefaults.AllowedHours),
        DemurrageRatePerDay = Money.FromMajor(
            (decimal)Number(args, "--rate", CharterPartyDefaults.RatePerDay),
            CharterPartyDefaults.Currency),
        // The document supplies the notice, so nothing is assumed here -- unlike `laytime`,
        // which has to substitute arrival when no charter party is to hand.
        NoticeOfReadinessUtc = sof.First(SofEventKind.NoticeOfReadinessTendered)?.TimestampUtc
            ?? call.ArrivedUtc,
        NoticeOfReadinessIsAssumed = sof.First(SofEventKind.NoticeOfReadinessTendered) is null,
        TurnTimeHours = Number(args, "--turn", CharterPartyDefaults.TurnHours),
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
    Console.WriteLine($"  matched   : {match.Explain()}");

    if (selection.AlsoOverlapping.Count > 0)
    {
        // The losing candidates, printed rather than dropped. Usually a neighbouring visit clipped
        // at the edge of the document's span; occasionally a vessel that called twice in a week,
        // which the reader can tell apart and the pipeline cannot (ADR-0035).
        Console.WriteLine(
            $"              chosen over {selection.AlsoOverlapping.Count} other overlapping call(s):");
        foreach (var (other, overlap) in selection.AlsoOverlapping)
        {
            Console.WriteLine(
                $"                call {other.Id}  {other.ArrivedUtc:yyyy-MM-dd HH:mm} -> " +
                $"{other.DepartedUtc:yyyy-MM-dd HH:mm}  overlap {overlap.TotalHours:F1}h");
        }
    }

    if (!match.PortWasChecked)
    {
        // Half the evidence a human would use, and unavailable: either the stop was too far from
        // any port to name, or this store was built before the gazetteer existed.
        Console.WriteLine(
            $"              the document names {match.DocumentPort}; AIS named no port for this " +
            "call, so the port was NOT checked -- only the times were.");
    }
    else
    {
        Console.WriteLine(
            $"  port      : document says {match.DocumentPort}; AIS nearest is {match.AisPort} " +
            $"({match.AisPortDistanceNm!.Value.ToString("F1", CultureInfo.InvariantCulture)} nm)");

        if (match.PortNamesAgree == false)
        {
            // A question, not a finding, and nothing refuses on it. The World Port Index
            // transliterates and documents use exonyms, so "Goteborg" and "Gothenburg" are the
            // same port and compare as different. Reported for a human to settle -- the position
            // rule 4 takes when two sources disagree.
            Console.WriteLine(
                "              the two names do NOT agree. That may be an exonym or a local " +
                "spelling rather than a different port; the times overlap, so nothing is refused.");
        }
    }
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
    Console.WriteLine("  Counts are over what the store holds, not over lines read.");
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

    // R10 in its own right, not as a row above. It counts STOPS, and putting a stop count in the
    // same column as a row count would be a wrong number under a correct heading (ADR-0036).
    var disagreement = queries.StatusDisagreement();
    Console.WriteLine();

    if (disagreement.TotalStops == 0)
    {
        Console.WriteLine("  R10  no stops detected yet, so no status conflicts to count. Run detect.");
    }
    else
    {
        Console.WriteLine(
            $"  R10  {disagreement.Disagreeing:N0} of {disagreement.TotalStops:N0} stops " +
            $"({disagreement.Share!.Value:F1}%) report a status contradicting their own speed.");
        Console.WriteLine(
            "       Counted over stops, not rows: R10 is recorded on stop_event.status_agrees " +
            "rather than as a rejection or a flag. Both readings are kept; neither wins (rule 4).");
    }

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

static int Prune(string[] args)
{
    if (ValueOf(args, "--keep-days") is not { } rawDays
        || !int.TryParse(rawDays, NumberStyles.None, CultureInfo.InvariantCulture, out var keepDays)
        || keepDays <= 0)
    {
        Console.Error.WriteLine("prune needs --keep-days <positive whole number>");
        return 2;
    }

    var connection = ReadConnection.Resolve(ValueOf(args, "--postgres"), ValueOf(args, "--db"));

    if (connection.Dialect == SqlDialect.Sqlite && !File.Exists(connection.SqlitePath))
    {
        Console.Error.WriteLine($"no database at {connection.SqlitePath}");
        return 2;
    }

    var cutoff = DateTime.UtcNow.AddDays(-keepDays);
    var archiveDir = ValueOf(args, "--archive") ?? "archive";

    using var queries = connection.OpenQueries();

    // Everything that will not survive: a call arriving before the cutoff either disappears with
    // its fixes or is rebuilt truncated, and either way the record held now is the last complete
    // one. The export does NOT cover this -- it carries the lapse analysis, not the calls -- so
    // the archive is written here or the history is simply lost (ADR-0045).
    var losing = queries.ListPortCalls(new PortCallFilter { Limit = 100_000 })
        .Where(c => c.ArrivedUtc < cutoff)
        .OrderBy(c => c.ArrivedUtc)
        .ToList();

    Console.WriteLine($"cutoff: everything before {cutoff:yyyy-MM-dd HH:mm} UTC");
    Console.WriteLine($"  {losing.Count} port call(s) would no longer be derivable");

    if (!Present(args, "--force"))
    {
        Console.Error.WriteLine(
            $"  nothing removed. Re-run with --force to archive them to {archiveDir}/ and prune.");
        return 2;
    }

    using var store = OpenStore(args);
    store.EnsureSchema();

    // The order -- archive, read it back, and only then delete -- is decided in Core and tested
    // there without a database (ADR-0046). What is left here is fetching and printing.
    var result = new PrunePass(store, new JsonFileArchive(archiveDir)).Run(
        cutoff,
        DateTime.UtcNow,
        losing,
        queries.ListRuns(),
        queries.GetPhasesForPortCalls([.. losing.Select(c => c.Id)]));

    if (result.RefusedBecause is { } refusal)
    {
        Console.Error.WriteLine($"  {refusal}");
        return 1;
    }

    Console.WriteLine(result.ArchivePath is { } written
        ? $"  archived to {written} ({new FileInfo(written).Length / 1024.0:F0} KB), read back and verified"
        : "  no port call to archive; pruning fixes only");

    Console.WriteLine($"  removed {result.FixesRemoved:N0} position report(s) and every projection");
    Console.WriteLine("  run detect to rebuild stops and port calls from what remains.");

    return 0;
}

static int ReadArchive(string[] args)
{
    if (args.Length == 0 || args[0].StartsWith("--", StringComparison.Ordinal))
    {
        Console.Error.WriteLine("archive needs a file: ais archive <file.json>");
        return 2;
    }

    var path = args[0];

    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"no archive at {path}");
        return 2;
    }

    ArchiveDocument document;

    try
    {
        document = ArchiveJson.ReadFile(path);
    }
    catch (Exception e) when (e is InvalidDataException or IOException)
    {
        // Loud, and non-zero. An unreadable archive is the worst state this project has -- the
        // rows are gone and the record of them will not parse -- so it must never be mistaken
        // for an empty one.
        Console.Error.WriteLine(e.Message);
        return 1;
    }

    var calls = document.PortCalls;
    var mmsi = OptionalMmsi(args);

    if (mmsi is { } only)
    {
        calls = [.. calls.Where(c => c.Mmsi == only)];
    }

    Console.WriteLine(
        $"{path}: {document.PortCalls.Count} port call(s) archived " +
        $"{document.ArchivedUtc:yyyy-MM-dd HH:mm} UTC");
    Console.WriteLine(
        $"  cutoff    : everything arriving before {document.CutoffUtc:yyyy-MM-dd HH:mm} UTC");
    Console.WriteLine(
        $"  source    : {string.Join(", ", document.SourceFiles)}");

    if (calls.Count == 0)
    {
        Console.WriteLine();
        Console.WriteLine($"  no call in this archive is vessel {mmsi}.");
        return 0;
    }

    Console.WriteLine();
    Console.WriteLine(
        "  id     mmsi       arrived (UTC)      waiting  working  unclassified  nearest port");

    foreach (var call in calls)
    {
        Console.WriteLine(
            $"  {call.Id,-6} {call.Mmsi,-10} {call.ArrivedUtc:yyyy-MM-dd HH:mm}  " +
            $"{call.WaitingHours,9:F1}{call.WorkingHours,9:F1}" +
            $"{call.UnclassifiedHours,14:F1}  {ArchivedPort(call)}" +
            $"{(call.IsComplete ? "" : "  (open)")}");

        if (mmsi is not null)
        {
            // One vessel asked for by name gets its phases too. Across the whole archive they
            // would be thousands of lines; for one vessel they are the answer to the question
            // that made someone open a pruned record at all.
            foreach (var phase in call.Phases)
            {
                Console.WriteLine(
                    $"         {phase.Sequence}. {phase.Phase,-10} " +
                    $"{phase.StartedUtc:yyyy-MM-dd HH:mm} -> {phase.EndedUtc:yyyy-MM-dd HH:mm}  " +
                    $"{Hours(phase.IsComplete ? phase.ObservedDurationHours : null, phase.ObservedDurationHours)}h  " +
                    $"{phase.ReportedStatus ?? "(no status)"}" +
                    $"{(phase.StatusAgrees ? "" : "  (contradicted by its own speed)")}");
            }
        }
    }

    var open = calls.Count(c => !c.IsComplete);
    if (open > 0)
    {
        Console.WriteLine();
        Console.WriteLine(
            $"  {open} marked (open): true extent unknown and now permanently so. An incomplete " +
            "call could once be finished by ingesting the days around it; the fixes are gone.");
    }

    Console.WriteLine();
    Console.WriteLine(
        "  This is the record itself, not a projection of one. Nothing can rebuild these calls.");

    return 0;
}

/// <summary>An archived call's port, marked the same way a live one is (ADR-0034).</summary>
static string ArchivedPort(ArchivedPortCall call) =>
    call.PortName is null
        ? "(none named)"
        : $"{(call.PortDistanceNm <= PortAttributionThresholds.PlausiblyAtPortNm ? " " : "~")}" +
          $"{call.PortName} {call.PortDistanceNm!.Value.ToString("F1", CultureInfo.InvariantCulture)} nm";

static int Export(string[] args)
{
    if (ValueOf(args, "--out") is not { } outDir)
    {
        Console.Error.WriteLine("export needs --out <dir>");
        return 2;
    }

    var connection = ReadConnection.Resolve(ValueOf(args, "--postgres"), ValueOf(args, "--db"));

    if (connection.Dialect == SqlDialect.Sqlite && !File.Exists(connection.SqlitePath))
    {
        Console.Error.WriteLine($"no database at {connection.SqlitePath}; run ingest and detect first");
        return 2;
    }

    using var queries = connection.OpenQueries();

    var runs = queries.ListRuns();
    if (runs.Count == 0)
    {
        Console.Error.WriteLine("no ingest runs in this database; nothing to export");
        return 2;
    }

    var stopStatus = queries.StopStatusByVessel();
    var fixStatus = queries.FixStatusByVessel();
    var stops = queries.ListStops(new StopFilter { Limit = 5_000 });
    var portCalls = queries.ListPortCalls(new PortCallFilter { Limit = 5_000 });

    // Rebuilt into domain calls so the export can ask the laytime assessor how many of them it
    // would decline, and why. One phase query for the whole set rather than one per call.
    var phasesByCall = queries.GetPhasesForPortCalls([.. portCalls.Select(c => c.Id)])
        .GroupBy(p => p.Phase.PortCallId)
        .ToDictionary(g => g.Key, g => (IReadOnlyList<(StoredPhase, StoredStop)>)[.. g]);

    var callsForPricing = portCalls
        .Select(c => PortCallRebuilder.FromStored(
            c, phasesByCall.GetValueOrDefault(c.Id, [])))
        .ToList();

    // Names for the vessels that appear, not the whole register: the document is downloaded by a
    // browser, and ten thousand unused names is most of the file.
    var named = queries.GetVessels(
        [.. stopStatus.Where(a => a.Lapses > 0).Select(a => a.Mmsi)
            .Concat(fixStatus.Select(d => d.Mmsi)).Distinct()])
        .ToDictionary(v => v.Mmsi);

    var document = ExportBuilder.Build(
        DateTime.UtcNow,
        runs,
        QualityReport.Build(RuleRegistry.Default(), queries.QualityReport()),
        queries.StatusDisagreement(),
        stopStatus,
        fixStatus,
        named,
        portCalls.Count,
        stops.Count > 0 ? stops.Min(s => s.StartedUtc) : DateTime.UnixEpoch,
        stops.Count > 0 ? stops.Max(s => s.EndedUtc) : DateTime.UnixEpoch,
        queries.PortCallHoursForBenchmarks(),
        callsForPricing,
        queries.EtaHorizon());

    Directory.CreateDirectory(outDir);
    var path = Path.Combine(outDir, "pipeline.json");

    var options = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    File.WriteAllText(path, JsonSerializer.Serialize(document, options));

    var bytes = new FileInfo(path).Length;
    Console.WriteLine($"wrote {path}  ({bytes / 1024.0:F0} KB)");
    Console.WriteLine(
        $"  window    : {document.Manifest.FirstFixUtc:yyyy-MM-dd} to " +
        $"{document.Manifest.LastFixUtc:yyyy-MM-dd}  ({document.Manifest.SourceFiles.Count} source file(s))");
    Console.WriteLine(
        $"  lapses    : {document.Summary.ArrivalLapses:N0} on arrival (R10), " +
        $"{document.Summary.DepartureLapses:N0} on departure (R12)");
    Console.WriteLine(
        $"  habits    : {document.Summary.VesselsAlwaysWrong} vessels never updated the dial, " +
        $"{document.Summary.VesselsAlwaysRight} never got it wrong " +
        $"(at least {ExportSummary.HabitMinimumStops} stops each)");
    Console.WriteLine($"  vessels   : {document.Vessels.Count} with at least one lapse");

    var ports = document.Ports;
    Console.WriteLine(
        $"  ports     : {ports.Count} with a call, " +
        $"{ports.Count(p => p.MedianWaitingHours is not null)} with enough usable calls for a median " +
        $"(needs {BenchmarkMinimums.ForMedian}), " +
        $"{ports.Sum(p => p.UsableCalls):N0} usable of {ports.Sum(p => p.AttributedCalls):N0} attributed");

    // The refusals are reported beside the count, not behind it. A figure saying AIS prices port
    // calls, without saying how many it declines, would overstate what the pipeline does.
    var priceable = document.Priceability;
    Console.WriteLine(
        $"  priceable : {priceable.Priceable:N0} of {priceable.CallsAssessed:N0} port calls; " +
        $"{priceable.NoBerthPhase:N0} never went alongside, " +
        $"{priceable.BerthGeometryUntrustworthy:N0} have berth hours the pipeline will not stand behind");

    if (!priceable.IsBalanced)
    {
        // The same bar ingest holds itself to: a tally that does not decompose is a tally with an
        // outcome nobody named, and publishing it would put an unexplained number on a web page.
        Console.Error.WriteLine(
            $"ACCOUNTING ERROR: {priceable.CallsAssessed} calls assessed but " +
            $"{priceable.Priceable + priceable.NoBerthPhase + priceable.BerthGeometryUntrustworthy} " +
            "accounted for");
        return 1;
    }

    return 0;
}

/// <summary>
/// The nearest port, always with its distance.
///
/// A bare name would read as "the vessel was here". The distance is what separates a berth at
/// Arhus, 0.11 nm from its reference point, from an anchorage 14 nm off Kalundborg -- and a
/// tilde marks the ones too far out to call the port's own (ADR-0034).
/// </summary>
static string Port(StoredPortCall call) =>
    call.PortName is null
        ? "(none named)"
        : $"{(call.PlausiblyAtPort == true ? " " : "~")}{call.PortName} " +
          $"{call.PortDistanceNm!.Value.ToString("F1", CultureInfo.InvariantCulture)} nm";

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
            $"  exactly {limit} row(s) returned, which is the limit -- there are probably more.");
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
        Console.WriteLine($"  {open} have an unknown true extent, so their hours are lower bounds.");
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
    Console.WriteLine(
        "  id     mmsi       arrived (UTC)      waiting  working  unclassified  nearest port");

    foreach (var call in calls)
    {
        Console.WriteLine(
            $"  {call.Id,-6} {call.Mmsi,-10} {call.ArrivedUtc:yyyy-MM-dd HH:mm}  " +
            $"{call.WaitingHours,9:F1}{call.WorkingHours,9:F1}" +
            $"{call.UnclassifiedHours,14:F1}  {Port(call)}{(call.IsComplete ? "" : "  (open)")}");
    }

    var unclassified = calls.Sum(c => c.UnclassifiedHours);
    if (unclassified > 0)
    {
        // Neither waiting nor working. Counting these as either would favour one party to a
        // charter over the other, so they are carried as their own column (ADR-0025).
        Console.WriteLine();
        Console.WriteLine(
            $"  {unclassified:F1}h sit in phases whose geometry the pipeline does not stand behind: " +
            "neither waiting nor working, and not billable as either.");
    }

    var open = calls.Count(c => !c.IsComplete);
    if (open > 0)
    {
        Console.WriteLine();
        Console.WriteLine(
            $"  {open} marked (open): true extent unknown, hours are lower bounds. " +
            "--complete-only excludes them.");
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
