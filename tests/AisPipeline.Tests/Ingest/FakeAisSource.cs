using AisPipeline.Core.Domain;
using AisPipeline.Core.Ports;

namespace AisPipeline.Tests.Ingest;

/// <summary>Replayable source over fixed rows, as <see cref="IAisSource"/> requires.</summary>
internal sealed class FakeAisSource : IAisSource
{
    private readonly IReadOnlyList<string> _rows;

    public FakeAisSource(params string[] rows) => _rows = rows;

    public string SourceName => "fake.csv";

    public int ReadCount { get; private set; }

    public IEnumerable<AisSourceLine> ReadLines()
    {
        ReadCount++;
        for (var i = 0; i < _rows.Count; i++)
        {
            yield return new AisSourceLine(SourceName, i + 2, _rows[i], _rows[i].Split(','));
        }
    }
}

/// <summary>
/// A source that yields fewer rows the second time. Stands in for a live feed or a paginated
/// API -- anything the two-pass design cannot legitimately be used with.
/// </summary>
internal sealed class NonReplayableSource : IAisSource
{
    private readonly IReadOnlyList<string> _rows;
    private int _reads;

    public NonReplayableSource(params string[] rows) => _rows = rows;

    public string SourceName => "drifting.csv";

    public IEnumerable<AisSourceLine> ReadLines()
    {
        var take = _reads++ == 0 ? _rows.Count : _rows.Count - 1;
        for (var i = 0; i < take; i++)
        {
            yield return new AisSourceLine(SourceName, i + 2, _rows[i], _rows[i].Split(','));
        }
    }
}
