using System.Globalization;
using AisPipeline.Adapters.Csv;
using AisPipeline.Adapters.Sqlite;
using AisPipeline.Core.Ingest;
using AisPipeline.Core.Quality;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Usage();
    return 0;
}

return args[0] switch
{
    "ingest" => Ingest(args[1..]),
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

      ais ingest <file.csv|file.zip> [--db <path>] [--ship-type <type>] [--limit <n>]

    Options:
      --db          SQLite file to write (default: data/ais.db)
      --ship-type   keep only vessels whose resolved type matches, e.g. Tanker
      --limit       stop after N source lines; for the development loop only
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

    var database = ValueOf(args, "--db") ?? Path.Combine("data", "ais.db");
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

    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(database))!);

    using var store = new SqliteAisStore(database);
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

static string? ValueOf(string[] args, string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
