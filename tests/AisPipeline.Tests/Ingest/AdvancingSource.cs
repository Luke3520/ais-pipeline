using AisPipeline.Core.Domain;
using AisPipeline.Core.Ports;
using Microsoft.Extensions.Time.Testing;

namespace AisPipeline.Tests.Ingest;

/// <summary>
/// Replayable source that advances a test clock as it is read, so a run takes measurable time
/// without the test sleeping.
/// </summary>
internal sealed class AdvancingSource : IAisSource
{
    private readonly FakeTimeProvider _time;
    private readonly IReadOnlyList<string> _rows;

    public AdvancingSource(FakeTimeProvider time, params string[] rows)
    {
        _time = time;
        _rows = rows;
    }

    public string SourceName => "advancing.csv";

    public IEnumerable<AisSourceLine> ReadLines()
    {
        for (var i = 0; i < _rows.Count; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            yield return new AisSourceLine(SourceName, i + 2, _rows[i], _rows[i].Split(','));
        }
    }
}
