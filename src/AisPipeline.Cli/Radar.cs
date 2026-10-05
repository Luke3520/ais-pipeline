using System.Diagnostics;
using System.Globalization;
using System.Text;
using AisPipeline.Adapters.Csv;
using AisPipeline.Adapters.Narration;
using AisPipeline.Adapters.Sql;
using AisPipeline.Core.Narration;
using AisPipeline.Core.Ports;
using AisPipeline.Core.Query;
using AisPipeline.Core.Radar;

/// <summary>
/// `ais radar` and `ais log`: the stored track played back on a green screen, and the ship's log
/// that scrolls beneath it.
///
/// A view, and only a view (ADR-0049). It reads through the same query port as every other verb
/// and writes nothing. Every judgment it shows -- stopped, contradicting itself, at a named port --
/// was made by detection and the quality rules and is read here, not re-made.
/// </summary>
internal static class RadarCommand
{
    /// <summary>One wall-clock revolution of the beam. A real scanner turns in two to four.</summary>
    private const double SweepPeriodSeconds = 4.0;

    /// <summary>Feed seconds per wall second, unless --speed says otherwise: ten minutes a second.</summary>
    private const double DefaultSpeed = 600.0;

    private const double DefaultHours = 24.0;
    private const int LogLines = 6;
    private const int FramesPerSecond = 20;

    /// <summary>The teletype types this many characters a second; a backlog prints instantly.</summary>
    private const double TeletypeCharsPerSecond = 160.0;

    public static int Radar(string[] args)
    {
        if (Open(args) is not { } queries)
        {
            return 2;
        }

        using (queries)
        {
            var regionName = Value(args, "--region") ?? "danish-waters";
            if (!RadarRegions.ByName.TryGetValue(regionName, out var region))
            {
                Console.Error.WriteLine(
                    $"unknown region '{regionName}'. Known: {string.Join(", ", RadarRegions.ByName.Keys)}");
                return 2;
            }

            if (Window(args, queries) is not { } window)
            {
                return 2;
            }

            var coastPath = Value(args, "--coast") ?? CoastlineCsv.DefaultPath;
            if (!File.Exists(coastPath))
            {
                Console.Error.WriteLine(
                    $"no coastline at {coastPath}. Run from the repository root, or pass --coast <file>.");
                return 2;
            }

            var coast = CoastlineCsv.Load(coastPath);
            var palette = Present(args, "--amber") ? Palette.Amber : Palette.Green;
            var names = new NameCache(queries);

            if (Value(args, "--at") is { } atRaw)
            {
                if (!TryInstant(atRaw, out var at))
                {
                    Console.Error.WriteLine($"--at needs an ISO-8601 UTC instant, got '{atRaw}'");
                    return 2;
                }

                return Once(queries, region, coast, at, names, palette);
            }

            var speed = Number(args, "--speed", DefaultSpeed);
            if (speed <= 0)
            {
                Console.Error.WriteLine("--speed needs a positive number of feed seconds per second");
                return 2;
            }

            if (Console.IsOutputRedirected)
            {
                Console.Error.WriteLine("the replay needs a terminal. For one frame to a file, pass --at <instant>.");
                return 2;
            }

            return Replay(queries, region, regionName, coast, window, speed, names, palette);
        }
    }

    public static int Log(string[] args)
    {
        if (Open(args) is not { } queries)
        {
            return 2;
        }

        using (queries)
        {
            if (Value(args, "--mmsi") is not { } raw
                || !long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var mmsi))
            {
                Console.Error.WriteLine("log needs --mmsi <n>");
                return 2;
            }

            if (queries.FeedWindow() is not { } feed)
            {
                Console.Error.WriteLine("the store is empty; run ingest and detect first");
                return 2;
            }

            var to = feed.LastUtc.AddSeconds(1);
            var replay = new RadarReplay(
                queries.StopsOverlapping(feed.FirstUtc, to, mmsi),
                queries.PortCallsOverlapping(mmsi, feed.FirstUtc, to),
                new NameCache(queries).Lookup);

            var entries = queries.Track(feed.FirstUtc, to, mmsi).SelectMany(replay.Advance).ToList();
            var name = queries.GetVessel(mmsi)?.Name?.Trim() ?? "(unnamed)";

            Console.WriteLine($"SHIP'S LOG  {name}  mmsi {mmsi}");
            Console.WriteLine($"{feed.FirstUtc:yyyy-MM-dd HH:mm:ss} to {feed.LastUtc:yyyy-MM-dd HH:mm:ss} UTC");
            Console.WriteLine();

            if (entries.Count == 0)
            {
                Console.WriteLine("  no entries: this vessel neither stopped nor contradicted itself in the window.");
                return 0;
            }

            if (Present(args, "--narrate") && Narrate(name, entries, Value(args, "--model") ?? ClaudeNarrator.DefaultModel))
            {
                return 0;
            }

            foreach (var e in entries)
            {
                Console.WriteLine($"  {e.Render(withDate: true)}");
            }

            Console.WriteLine();
            Console.WriteLine("  [rN·LM] is ingest run N, source line M. Check any line with:");
            Console.WriteLine("  SELECT * FROM position_report WHERE ingest_run_id = N AND source_line = M;");
            return 0;
        }
    }

    /// <summary>
    /// The log in a master's voice, printed only if every sentence survives the validator. Returns
    /// false when it printed nothing but the reason, so the caller falls through to the
    /// deterministic log. The reader always gets a log, and always learns which kind it is.
    /// </summary>
    private static bool Narrate(string vesselName, IReadOnlyList<LogEntry> entries, string model)
    {
        if (!ClaudeNarrator.Configured())
        {
            Console.WriteLine("  --narrate needs ANTHROPIC_API_KEY (or ANTHROPIC_AUTH_TOKEN). The plain log follows.");
            Console.WriteLine();
            return false;
        }

        INarrator narrator = new ClaudeNarrator(model);
        var reply = narrator
            .NarrateAsync(NarrationPrompt.System, NarrationPrompt.User(vesselName, entries), CancellationToken.None)
            .GetAwaiter().GetResult();

        if (reply.Text is not { } text)
        {
            Console.WriteLine($"  NARRATION UNAVAILABLE: {reply.Declined}. The plain log follows.");
            Console.WriteLine();
            return false;
        }

        var verdict = CitedNarrative.Validate(text, entries);
        if (!verdict.Accepted)
        {
            // Refused whole. Printing the sentences that passed would present a narration the
            // model did not write and the validator did not accept: the good half of a bad answer.
            Console.WriteLine($"  NARRATION REFUSED: {verdict.Problems.Count} problem(s) in what {narrator.Name} wrote.");
            foreach (var problem in verdict.Problems)
            {
                Console.WriteLine($"    - {problem}");
            }

            Console.WriteLine("  The plain log follows.");
            Console.WriteLine();
            return false;
        }

        foreach (var sentence in verdict.Sentences)
        {
            Console.WriteLine($"  {sentence}");
        }

        var citations = verdict.Sentences.Sum(s => AisPipeline.Core.Domain.Citation.FindAll(s).Count);
        Console.WriteLine();
        Console.WriteLine($"  Written by {narrator.Name}. Checked: {verdict.Sentences.Count} sentences, {citations} citations,");
        Console.WriteLine("  every one in the log above it, and every number in each sentence found in the lines it cites.");
        Console.WriteLine("  Not checkable: a claim made of words alone. Run without --narrate for the source log.");
        return true;
    }

    /// <summary>One frame at an instant, for scripts and the README. Plain text when redirected.</summary>
    private static int Once(IAisQueries queries, GeoBox region,
        IReadOnlyList<IReadOnlyList<(double, double)>> coast, DateTime at, NameCache names, Palette palette)
    {
        // State at an instant needs the trail behind it: an hour of fixes, plus every stop and call
        // that hour touches. Nothing earlier changes what is on the scope.
        var from = at - RadarThresholds.TrailDuration;
        var to = at.AddSeconds(1);
        var replay = new RadarReplay(
            queries.StopsOverlapping(from, to, mmsi: null),
            queries.PortCallsOverlapping(from, to),
            names.Lookup);

        var log = queries.Track(from, to, mmsi: null).SelectMany(replay.Advance).ToList();

        var (columns, rows) = Size(fallbackColumns: 100, fallbackRows: 34);
        var screen = Compose(region, coast, replay, log, sweepDeg: 45, columns, rows,
            header: $"AIS RADAR -- {at:yyyy-MM-dd HH:mm:ss}Z", typed: int.MaxValue);

        Console.Write(Console.IsOutputRedirected ? screen.Plain() : screen.Ansi(palette));
        Console.WriteLine();
        return 0;
    }

    private static int Replay(IAisQueries queries, GeoBox region, string regionName,
        IReadOnlyList<IReadOnlyList<(double, double)>> coast, FeedWindow window, double speed,
        NameCache names, Palette palette)
    {
        var quit = false;
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            quit = true;
        };

        // Alternate screen and a hidden cursor, restored on every exit path including Ctrl-C.
        Console.Write("\e[?1049h\e[?25l\e[2J\e[H");
        try
        {
            Console.Write(palette.Text + "\n  warming up the magnetron...\n\n  " +
                "(sorting the window by time; a day of the full feed takes a few seconds)" + Ansi.Reset);

            var replay = new RadarReplay(
                queries.StopsOverlapping(window.FirstUtc, window.LastUtc, mmsi: null),
                queries.PortCallsOverlapping(window.FirstUtc, window.LastUtc),
                names.Lookup);

            using var fixes = queries.Track(window.FirstUtc, window.LastUtc, mmsi: null).GetEnumerator();
            var pending = fixes.MoveNext() ? fixes.Current : null;

            var log = new List<LogEntry>();
            var typedAt = Stopwatch.StartNew();
            var typedCount = 0;
            var clock = window.FirstUtc;
            var paused = false;
            var wall = Stopwatch.StartNew();
            var last = wall.Elapsed;

            while (!quit)
            {
                var now = wall.Elapsed;
                var dt = (now - last).TotalSeconds;
                last = now;

                while (Console.KeyAvailable)
                {
                    switch (Console.ReadKey(intercept: true).Key)
                    {
                        case ConsoleKey.Q or ConsoleKey.Escape:
                            quit = true;
                            break;
                        case ConsoleKey.Spacebar:
                            paused = !paused;
                            break;
                        case ConsoleKey.OemPlus or ConsoleKey.Add or ConsoleKey.UpArrow:
                            speed *= 2;
                            break;
                        case ConsoleKey.OemMinus or ConsoleKey.Subtract or ConsoleKey.DownArrow:
                            speed = Math.Max(1, speed / 2);
                            break;
                    }
                }

                if (!paused && pending is not null)
                {
                    clock = clock.AddSeconds(dt * speed);
                    while (pending is not null && pending.TimestampUtc <= clock)
                    {
                        log.AddRange(replay.Advance(pending));
                        pending = fixes.MoveNext() ? fixes.Current : null;
                    }
                }

                // The teletype: the newest line types itself out. Fall more than a line behind and
                // the backlog prints at once, because a log that lags the scope is a log that lies
                // about what just happened.
                if (log.Count > typedCount + 1)
                {
                    typedCount = log.Count - 1;
                    typedAt.Restart();
                }

                var typed = log.Count > typedCount
                    ? (int)(typedAt.Elapsed.TotalSeconds * TeletypeCharsPerSecond)
                    : int.MaxValue;
                if (log.Count > typedCount && typed > log[^1].Render(withDate: false).Length)
                {
                    typedCount = log.Count;
                }

                var state = pending is null ? "END OF TAPE" : paused ? "PAUSED" : $"x{speed:0}";
                var sweep = now.TotalSeconds % SweepPeriodSeconds / SweepPeriodSeconds * 360.0;
                var (columns, rows) = Size(fallbackColumns: 100, fallbackRows: 34);
                var screen = Compose(region, coast, replay, log, sweep, columns, rows,
                    header: string.Create(CultureInfo.InvariantCulture,
                        $"AIS RADAR -- {regionName.ToUpperInvariant()} -- {clock:yyyy-MM-dd HH:mm:ss}Z -- {state}"),
                    typed);

                Console.Write("\e[H" + screen.Ansi(palette));
                Thread.Sleep(1000 / FramesPerSecond);
            }
        }
        finally
        {
            Console.Write(Ansi.Reset + "\e[?25h\e[?1049l");
        }

        return 0;
    }

    private static Screen Compose(GeoBox region, IReadOnlyList<IReadOnlyList<(double, double)>> coast,
        RadarReplay replay, List<LogEntry> log, double sweepDeg, int columns, int rows,
        string header, int typed)
    {
        var scope = replay.Scope();
        // Header, rule, status, the log and the footer: everything that is not radar. Off by one
        // here scrolls the terminal by a line every frame and pushes the header off the top.
        const int Chrome = 4;
        var radarRows = Math.Max(4, rows - LogLines - Chrome);

        // The vessels the log just spoke about are the ones named on the scope, so a line in the
        // log and a blip on the chart can be matched by eye.
        var labelled = log.AsEnumerable().Reverse().Select(e => e.Mmsi).Distinct()
            .Where(m => scope.Any(v => v.Mmsi == m)).ToList();

        var frame = RadarFrameBuilder.Build(new RadarScene(
            region, columns, radarRows, coast, scope, sweepDeg, labelled));

        var contacts = scope.Count;
        var conflicts = scope.Count(v => v.State is ScopeState.StoppedClaimingUnderWay or ScopeState.MovingClaimingStationary);
        var status = string.Create(CultureInfo.InvariantCulture,
            $"{contacts} contacts, {conflicts} contradicting themselves -- rings {frame.RingSpacingNm:0.#} nm");

        var tail = log.Count <= LogLines ? log : log.GetRange(log.Count - LogLines, LogLines);
        var lines = tail.Select((e, i) =>
        {
            var text = e.Render(withDate: false);
            return i == tail.Count - 1 && typed < text.Length ? text[..typed] + "█" : text;
        }).ToList();

        return new Screen(header, status, frame, lines, columns);
    }

    private static IAisQueries? Open(string[] args)
    {
        var connection = ReadConnection.Resolve(Value(args, "--postgres"), Value(args, "--db"));
        if (connection.Dialect == SqlDialect.Sqlite && !File.Exists(connection.SqlitePath))
        {
            Console.Error.WriteLine($"no database at {connection.SqlitePath}; run ingest and detect first");
            return null;
        }

        return connection.OpenQueries();
    }

    private static FeedWindow? Window(string[] args, IAisQueries queries)
    {
        if (queries.FeedWindow() is not { } feed)
        {
            Console.Error.WriteLine("the store is empty; run ingest and detect first");
            return null;
        }

        var from = feed.FirstUtc;
        if (Value(args, "--from") is { } raw && !TryInstant(raw, out from))
        {
            Console.Error.WriteLine($"--from needs an ISO-8601 UTC instant, got '{raw}'");
            return null;
        }

        var hours = Number(args, "--hours", DefaultHours);
        var to = from.AddHours(hours) < feed.LastUtc ? from.AddHours(hours) : feed.LastUtc.AddSeconds(1);
        return new FeedWindow { FirstUtc = from, LastUtc = to };
    }

    private static bool TryInstant(string raw, out DateTime utc) => DateTime.TryParse(
        raw, CultureInfo.InvariantCulture,
        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out utc);

    private static (int Columns, int Rows) Size(int fallbackColumns, int fallbackRows)
    {
        try
        {
            return Console.IsOutputRedirected || Console.WindowWidth <= 0
                ? (fallbackColumns, fallbackRows)
                : (Console.WindowWidth, Console.WindowHeight);
        }
        catch (IOException)
        {
            return (fallbackColumns, fallbackRows);
        }
    }

    private static string? Value(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static bool Present(string[] args, string name) => Array.IndexOf(args, name) >= 0;

    private static double Number(string[] args, string name, double fallback) =>
        Value(args, name) is { } raw
         && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v
            : fallback;

    /// <summary>Vessel names, looked up the first time a vessel appears and remembered.</summary>
    private sealed class NameCache(IAisQueries queries)
    {
        private readonly Dictionary<long, string?> _names = [];

        public string? Lookup(long mmsi)
        {
            if (!_names.TryGetValue(mmsi, out var name))
            {
                name = queries.GetVessel(mmsi)?.Name;
                _names[mmsi] = name;
            }

            return name;
        }
    }

    private static class Ansi
    {
        public const string Reset = "\e[0m";

        public static string Fg(int colour256) => $"\e[38;5;{colour256}m";
    }

    /// <summary>
    /// Phosphor colours, in xterm's 256-colour cube. P1 green is what radar scopes and the VT100
    /// shipped with; P3 amber is what the people who stared at them all day asked for instead.
    /// </summary>
    private sealed record Palette(int Dim, int Mid, int Bright, int Hot, int Conflict, int ConflictLit)
    {
        public static readonly Palette Green = new(22, 28, 34, 46, 172, 214);
        public static readonly Palette Amber = new(94, 130, 172, 214, 160, 196);

        public string Text => Ansi.Fg(Bright);

        public int Colour(Cell cell) => (cell.Ink, cell.Lit) switch
        {
            (Ink.Conflict, true) => ConflictLit,
            (Ink.Conflict, false) => Conflict,
            (Ink.Beam, _) => Hot,
            (Ink.Moving or Ink.Stopped, true) => Hot,
            (Ink.Moving or Ink.Stopped, false) => Bright,
            (Ink.Label, _) => Bright,
            (Ink.Coast, true) => Bright,
            (Ink.Coast, false) => Mid,
            (Ink.Trail, true) => Mid,
            _ => Dim,
        };
    }

    private sealed record Screen(string Header, string Status, RadarFrame Frame,
        IReadOnlyList<string> Log, int Columns)
    {
        public string Plain()
        {
            var sb = new StringBuilder();
            sb.AppendLine(Header);
            foreach (var row in Frame.Text())
            {
                sb.AppendLine(row.TrimEnd());
            }

            sb.AppendLine(Status);
            foreach (var line in Log)
            {
                sb.AppendLine(line);
            }

            return sb.ToString();
        }

        public string Ansi(Palette p)
        {
            var sb = new StringBuilder(Frame.Rows * Columns * 4);
            Line(sb, p.Text, Header);

            for (var r = 0; r < Frame.Rows; r++)
            {
                var current = -1;
                for (var c = 0; c < Frame.Columns; c++)
                {
                    var cell = Frame.Cells[r, c];
                    var colour = p.Colour(cell);
                    if (colour != current)
                    {
                        sb.Append(AnsiFg(colour));
                        current = colour;
                    }

                    sb.Append(cell.Glyph);
                }

                sb.Append("\e[K\n");
            }

            Line(sb, AnsiFg(p.Mid), new string('─', Math.Max(0, Columns - 1)));
            Line(sb, AnsiFg(p.Mid), Status);
            for (var i = 0; i < LogLines; i++)
            {
                Line(sb, p.Text, i < Log.Count ? Log[i] : "");
            }

            Line(sb, AnsiFg(p.Dim), "q quit   space pause   +/- speed", last: true);
            return sb.ToString();
        }

        private void Line(StringBuilder sb, string colour, string text, bool last = false)
        {
            var clipped = text.Length > Columns - 1 ? text[..(Columns - 1)] : text;
            sb.Append(colour).Append(clipped).Append("\e[K");
            sb.Append(last ? RadarCommand.Ansi.Reset : "\n");
        }

        private static string AnsiFg(int colour) => RadarCommand.Ansi.Fg(colour);
    }
}
