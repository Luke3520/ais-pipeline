using System.Globalization;
using AisPipeline.Adapters.Csv;
using AisPipeline.Adapters.Postgres;
using AisPipeline.Adapters.Sqlite;
using AisPipeline.Core.Annotate;
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

    Options:
      --db          SQLite file to read/write (default: data/ais.db)
      --postgres    Postgres connection string instead of SQLite. compose.yaml provides one:
                    Host=localhost;Port=55432;Database=ais;Username=ais;Password=ais
      --ship-type   keep only vessels whose resolved type matches, e.g. Tanker
      --limit       stop after N source lines; for the development loop only

    detect annotates the sequence rules and rebuilds stops and port calls. It is a full
    recompute: running it twice lands on exactly the same result.
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
    var annotated = new AnnotatePass(
        store,
        [new R7Teleport(), new R8CoverageGap(), new R11SpeedConsistency()]).Run();

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
