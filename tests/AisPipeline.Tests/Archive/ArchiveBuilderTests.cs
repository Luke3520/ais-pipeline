using AisPipeline.Core.Archive;
using AisPipeline.Core.Query;

namespace AisPipeline.Tests.Archive;

/// <summary>
/// The shape of the one document this pipeline cannot regenerate.
///
/// Everything else here is a projection and can be rebuilt by re-running detect. An archive is
/// written once, at the moment the rows behind it are deleted, so what it does and does not carry
/// is decided in Core and asserted here rather than left to the CLI (ADR-0045).
/// </summary>
public class ArchiveBuilderTests
{
    private static readonly DateTime T0 = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    private static StoredPortCall Call(long id, long mmsi, DateTime arrived, bool complete = true) =>
        new()
        {
            Id = id,
            Mmsi = mmsi,
            ArrivedUtc = arrived,
            DepartedUtc = arrived.AddHours(10),
            WaitingHours = 4.0,
            WorkingHours = 6.0,
            UnclassifiedHours = 0.0,
            IsComplete = complete,
            PortName = "Arhus",
            PortCountry = "DK",
            PortDistanceNm = 0.11,
        };

    private static (StoredPhase, StoredStop) Phase(long callId, int sequence, string phase, bool agrees = true) =>
        (new StoredPhase { Id = sequence, PortCallId = callId, StopId = sequence, Sequence = sequence, Phase = phase },
         new StoredStop
         {
             Id = sequence,
             StartedUtc = T0.AddHours(sequence),
             EndedUtc = T0.AddHours(sequence + 1),
             ObservedDurationHours = 1.0,
             IsComplete = true,
             ReportedStatus = "Under way using engine",
             StatusAgrees = agrees,
         });

    private static ArchiveDocument Build(
        IReadOnlyList<StoredPortCall> calls,
        IReadOnlyList<(StoredPhase, StoredStop)>? phases = null,
        IReadOnlyList<StoredRun>? runs = null) =>
        ArchiveBuilder.Build(
            T0.AddDays(90),
            T0.AddDays(7),
            runs ?? [new StoredRun { Id = 1, SourceFile = "day-1.zip", RowsRead = 100, RowsInserted = 40 }],
            calls,
            phases ?? []);

    [Fact]
    public void Stamps_the_format_version_it_wrote()
    {
        // A reader meeting this file years from now has no other way to ask what shape it is.
        Assert.Equal(ArchiveDocument.CurrentFormatVersion, Build([Call(1, 219018271, T0)]).FormatVersion);
    }

    [Fact]
    public void Carries_every_call_it_was_given()
    {
        var document = Build([Call(1, 111, T0), Call(2, 222, T0.AddDays(1)), Call(3, 333, T0.AddDays(2))]);

        // Not a sample and not a page. Anything left out here is deleted a moment later.
        Assert.Equal(3, document.PortCalls.Count);
    }

    [Fact]
    public void Orders_calls_by_arrival_rather_than_by_duration()
    {
        // Every live listing ranks by size, because it is answering "which are the big ones".
        // An archive answers "what happened", and that order is the one worth preserving.
        var document = Build([Call(3, 333, T0.AddDays(2)), Call(1, 111, T0), Call(2, 222, T0.AddDays(1))]);

        Assert.Equal([111, 222, 333], document.PortCalls.Select(c => c.Mmsi));
    }

    [Fact]
    public void Keeps_a_calls_phases_in_sequence()
    {
        var document = Build(
            [Call(1, 111, T0)],
            [Phase(1, 2, "Berth"), Phase(1, 1, "Anchorage")]);

        Assert.Equal([1, 2], document.PortCalls[0].Phases.Select(p => p.Sequence));
        Assert.Equal(["Anchorage", "Berth"], document.PortCalls[0].Phases.Select(p => p.Phase));
    }

    [Fact]
    public void Gives_each_call_only_its_own_phases()
    {
        var document = Build(
            [Call(1, 111, T0), Call(2, 222, T0.AddDays(1))],
            [Phase(1, 1, "Berth"), Phase(2, 1, "Anchorage"), Phase(2, 2, "Berth")]);

        Assert.Single(document.PortCalls[0].Phases);
        Assert.Equal(2, document.PortCalls[1].Phases.Count);
    }

    [Fact]
    public void A_call_with_no_phases_is_archived_anyway()
    {
        // It still happened, and this file is the last place it is written down. An empty phase
        // list is a smaller loss than a missing call.
        var document = Build([Call(1, 111, T0)], []);

        Assert.Single(document.PortCalls);
        Assert.Empty(document.PortCalls[0].Phases);
    }

    [Fact]
    public void Keeps_the_disagreement_rather_than_resolving_it()
    {
        // Rule 4. Both readings were stored and neither won; deleting the rows is not the moment
        // to start picking a winner.
        var document = Build([Call(1, 111, T0)], [Phase(1, 1, "Berth", agrees: false)]);

        Assert.False(document.PortCalls[0].Phases[0].StatusAgrees);
        Assert.Equal("Under way using engine", document.PortCalls[0].Phases[0].ReportedStatus);
    }

    [Fact]
    public void An_incomplete_call_says_so()
    {
        // And permanently: once the fixes are pruned, no later ingest can finish it.
        Assert.False(Build([Call(1, 111, T0, complete: false)]).PortCalls[0].IsComplete);
    }

    [Fact]
    public void Keeps_the_port_distance_with_the_port_name()
    {
        // A name without its distance claims more than the data supports (ADR-0034), and that
        // does not stop being true because the rows are gone.
        var call = Build([Call(1, 111, T0)]).PortCalls[0];

        Assert.Equal("Arhus", call.PortName);
        Assert.Equal(0.11, call.PortDistanceNm);
    }

    [Fact]
    public void Names_each_source_file_once()
    {
        // A file ingested twice records two runs by design. Listing both would make the archive
        // claim it came from more of the feed than exists -- the defect ADR-0039 found in the
        // export manifest.
        var document = Build(
            [Call(1, 111, T0)],
            runs:
            [new StoredRun { Id = 1, SourceFile = "day-1.zip", RowsRead = 100, RowsInserted = 40 },
             new StoredRun { Id = 2, SourceFile = "day-1.zip", RowsRead = 100, RowsInserted = 0 },
             new StoredRun { Id = 3, SourceFile = "day-2.zip", RowsRead = 60, RowsInserted = 20 }]);

        Assert.Equal(["day-1.zip", "day-2.zip"], document.SourceFiles);
    }

    [Fact]
    public void Carries_the_cutoff_it_was_written_for()
    {
        // Without it the file says what was kept but not what the boundary was, and a reader
        // cannot tell a short archive from a prune that nearly emptied the store.
        Assert.Equal(T0.AddDays(7), Build([Call(1, 111, T0)]).CutoffUtc);
    }
}
