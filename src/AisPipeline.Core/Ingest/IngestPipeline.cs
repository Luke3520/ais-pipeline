using AisPipeline.Core.Domain;
using AisPipeline.Core.Parsing;
using AisPipeline.Core.Ports;
using AisPipeline.Core.Quality;

namespace AisPipeline.Core.Ingest;

/// <summary>Outcome of one ingest run.</summary>
/// <param name="RunId">The ingest_run row every stored position references.</param>
/// <param name="Counters">Where every row read ended up.</param>
/// <param name="FeedRuleHits">Rule hit counts across the WHOLE feed, not just stored vessels.</param>
/// <param name="VesselsInScope">How many vessels passed the scope guard.</param>
public sealed record IngestResult(
    long RunId,
    IngestCounters Counters,
    IReadOnlyDictionary<string, long> FeedRuleHits,
    int VesselsInScope);

/// <summary>
/// Two-pass ingest.
///
/// Pass 1 reads every row to resolve vessel identity and to count rule hits across the whole
/// feed. Pass 2 reads again and stores positions for vessels that passed the scope guard.
///
/// The second read costs about a minute on a local file and is what stops the scope guard
/// silently discarding 2.6% of each tanker's fixes (ADR-0007).
/// </summary>
public sealed class IngestPipeline
{
    private readonly IAisStore _store;
    private readonly RuleRegistry _rules;
    private readonly IngestOptions _options;
    private readonly TimeProvider _time;

    /// <param name="time">
    /// Clock for the run's start and finish stamps. Injected rather than read from
    /// DateTime.UtcNow so Core carries no ambient dependency (ADR-0003) and so a test can
    /// assert that finished_utc genuinely advances past started_utc.
    /// </param>
    public IngestPipeline(
        IAisStore store,
        RuleRegistry rules,
        IngestOptions options,
        TimeProvider? time = null)
    {
        _store = store;
        _rules = rules;
        _options = options;
        _time = time ?? TimeProvider.System;
    }

    public IngestResult Run(IAisSource source)
    {
        _store.EnsureSchema();
        var startedUtc = _time.GetUtcNow().UtcDateTime;
        var runId = _store.BeginRun(source.SourceName, startedUtc);

        var pass1 = ResolveIdentities(source);
        _store.UpsertVessels([.. pass1.Vessels.Values]);

        var inScope = SelectInScope(pass1.Vessels);
        var counters = StorePositions(source, runId, inScope);

        // The two passes must have seen the same rows. If they did not, the scope set was
        // computed from one sample and applied to another, and a vessel's genuine fixes would
        // be counted as out-of-scope with nothing to distinguish that from a real filter --
        // silent loss. IAisSource requires replayability; this is the backstop that makes a
        // violation loud instead of invisible.
        if (counters.RowsRead != pass1.RowsRead)
        {
            throw new InvalidOperationException(
                $"source '{source.SourceName}' is not replayable: pass 1 read {pass1.RowsRead:N0} " +
                $"rows, pass 2 read {counters.RowsRead:N0}. Scope was resolved against rows that " +
                "pass 2 did not see, so any row count it reports is untrustworthy.");
        }

        // Read the clock again. Passing startedUtc here made finished_utc identical to
        // started_utc on every run, so the audit row claimed a 57-second ingest took no time.
        _store.CompleteRun(runId, _time.GetUtcNow().UtcDateTime, counters);
        return new IngestResult(runId, counters, pass1.RuleHits, inScope.Count);
    }

    private sealed record Pass1(
        Dictionary<long, Vessel> Vessels,
        Dictionary<string, long> RuleHits,
        long RowsRead);

    /// <summary>
    /// Pass 1: fold every row into a per-MMSI identity, and count every rule hit across the
    /// whole feed. The quality report describes all ~17M rows even though only the vessels in
    /// scope are stored, which is why these counts are gathered here rather than in pass 2.
    /// </summary>
    private Pass1 ResolveIdentities(IAisSource source)
    {
        var vessels = new Dictionary<long, Vessel>();
        var hits = new Dictionary<string, long>(StringComparer.Ordinal);
        long read = 0;

        foreach (var line in source.ReadLines())
        {
            if (_options.Limit is { } limit && read >= limit)
            {
                break;
            }

            read++;

            var parse = RawAisRecordParser.Parse(line);
            foreach (var hit in _rules.Judge(line, parse).Hits)
            {
                hits[hit.RuleId] = hits.GetValueOrDefault(hit.RuleId) + 1;
            }

            if (parse.Record is not { } record)
            {
                continue;
            }

            vessels[record.Mmsi] = vessels.TryGetValue(record.Mmsi, out var existing)
                ? existing.MergeWith(record)
                : Vessel.From(record);
        }

        return new Pass1(vessels, hits, read);
    }

    private HashSet<long> SelectInScope(Dictionary<long, Vessel> vessels)
    {
        if (_options.ShipType is not { } wanted)
        {
            return [.. vessels.Keys];
        }

        return [.. vessels
            .Where(v => string.Equals(v.Value.ShipType, wanted, StringComparison.OrdinalIgnoreCase))
            .Select(v => v.Key)];
    }

    /// <summary>
    /// Pass 2: apply the rules and store. Every row read is counted into exactly one bucket --
    /// filtered, quarantined, duplicate, or inserted -- so the run summary accounts for all of
    /// them (<see cref="IngestCounters.IsBalanced"/>).
    /// </summary>
    private IngestCounters StorePositions(IAisSource source, long runId, HashSet<long> inScope)
    {
        long read = 0, filtered = 0, inserted = 0, dupInFile = 0, dupPrior = 0, quarantined = 0;

        var seen = new HashSet<NaturalKey>();
        var batch = new List<AcceptedPosition>(_options.BatchSize);
        var rejects = new List<QuarantinedRow>(_options.BatchSize);

        void FlushRejects()
        {
            if (rejects.Count == 0)
            {
                return;
            }

            _store.InsertQuarantine(runId, rejects);
            rejects.Clear();
        }

        void Flush()
        {
            // Rejects flush on their own threshold too: a file that is mostly rejects would
            // otherwise never fill a position batch, and the list would grow to the whole file.
            FlushRejects();

            if (batch.Count == 0)
            {
                return;
            }

            var stored = _store.InsertPositions(runId, batch);
            inserted += stored;

            // Anything the store refused was already present before this run began: in-file
            // duplicates never reach the batch, having been caught by `seen` above.
            dupPrior += batch.Count - stored;
            batch.Clear();
        }

        foreach (var line in source.ReadLines())
        {
            if (_options.Limit is { } limit && read >= limit)
            {
                break;
            }

            read++;

            var parse = RawAisRecordParser.Parse(line);
            var judgement = _rules.Judge(line, parse);

            if (judgement.FirstRejection is { } rejection)
            {
                // Scope before refusal: a row for a vessel we are not storing is filtered, not
                // quarantined. Counting it as a reject would inflate every rule's hit rate by
                // the share of the feed that is out of scope.
                //
                // An unparseable row has no MMSI to scope by, so it is always quarantined --
                // which is correct: it is evidence the parser or the feed changed.
                if (parse.Record is { } rejected && !inScope.Contains(rejected.Mmsi))
                {
                    filtered++;
                    continue;
                }

                quarantined++;
                rejects.Add(new QuarantinedRow(line.SourceFile, line.LineNumber, line.RawText, rejection));

                if (rejects.Count >= _options.BatchSize)
                {
                    FlushRejects();
                }

                continue;
            }

            var record = parse.Record!;

            if (!inScope.Contains(record.Mmsi))
            {
                filtered++;
                continue;
            }

            var key = new NaturalKey(record.Mmsi, record.TimestampUtc, record.Latitude, record.Longitude);
            if (!seen.Add(key))
            {
                dupInFile++;
                continue;
            }

            batch.Add(new AcceptedPosition(record, judgement.FlagString));

            if (batch.Count >= _options.BatchSize)
            {
                Flush();
            }
        }

        Flush();

        return new IngestCounters
        {
            RowsRead = read,
            RowsFiltered = filtered,
            RowsInserted = inserted,
            RowsDuplicateInFile = dupInFile,
            RowsDuplicatePriorRun = dupPrior,
            RowsQuarantined = quarantined,
        };
    }
}
