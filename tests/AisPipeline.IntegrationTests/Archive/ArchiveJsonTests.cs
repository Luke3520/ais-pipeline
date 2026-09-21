using AisPipeline.Adapters.Archive;
using AisPipeline.Core.Archive;
using AisPipeline.Core.Query;

namespace AisPipeline.IntegrationTests.Archive;

/// <summary>
/// Proof that what prune writes can be read back.
///
/// The archive was written for months by an anonymous type that nothing in the codebase ever read,
/// and "the file is non-empty" was the whole of the verification. These tests are the claim that
/// replaced it: the bytes parse, every field survives the trip, and the failures that would
/// otherwise pass silently -- a truncated write, a shape from another version, a document with
/// nothing in it -- are refused by name (ADR-0045).
/// </summary>
public class ArchiveJsonTests
{
    private static readonly DateTime T0 = new(2026, 9, 1, 6, 30, 0, DateTimeKind.Utc);

    private static ArchiveDocument Sample() =>
        ArchiveBuilder.Build(
            T0.AddDays(90),
            T0.AddDays(7),
            [new StoredRun { Id = 1, SourceFile = "aisdk-2026-09-01.zip", RowsRead = 100, RowsInserted = 40 }],
            [new StoredPortCall
            {
                Id = 126,
                Mmsi = 245313000,
                ArrivedUtc = T0,
                DepartedUtc = T0.AddHours(126.5),
                WaitingHours = 0.0,
                WorkingHours = 126.5,
                UnclassifiedHours = 0.25,
                IsComplete = false,
                PortName = "Marstal",
                PortCountry = "DK",
                PortDistanceNm = 3.1,
            }],
            [(new StoredPhase { Id = 1, PortCallId = 126, StopId = 9, Sequence = 1, Phase = "Berth" },
              new StoredStop
              {
                  Id = 9,
                  Mmsi = 245313000,
                  StartedUtc = T0,
                  EndedUtc = T0.AddHours(126.5),
                  ObservedDurationHours = 126.5,
                  IsComplete = false,
                  ReportedStatus = "Under way using engine",
                  StatusAgrees = false,
              })]);

    [Fact]
    public void EveryFieldSurvivesTheRoundTrip()
    {
        // Compared as bytes rather than field by field: write, read, write again, and the two
        // documents must be identical. A field added to the archive later and forgotten by
        // either side would change the second rendering, where an assertion naming today's
        // fields would keep passing over the gap. This file is the only copy of what it carries,
        // so the test has to cover fields nobody has thought of yet.
        var json = ArchiveJson.Write(Sample());

        Assert.Equal(json, ArchiveJson.Write(ArchiveJson.Read(json)));
    }

    [Fact]
    public void TheDisagreementSurvivesTheRoundTrip()
    {
        // Rule 4 held across serialisation. The status and the flag contradicting it both come
        // back, and neither was resolved on the way out or the way in.
        var phase = ArchiveJson.Read(ArchiveJson.Write(Sample())).PortCalls[0].Phases[0];

        Assert.Equal("Under way using engine", phase.ReportedStatus);
        Assert.False(phase.StatusAgrees);
    }

    [Fact]
    public void AnIncompleteCallComesBackIncomplete()
    {
        // The bound has to survive as a bound. A false read back as true would turn a censored
        // 126.5 hours into a measured one, on a record nothing can re-derive.
        var call = ArchiveJson.Read(ArchiveJson.Write(Sample())).PortCalls[0];

        Assert.False(call.IsComplete);
        Assert.False(call.Phases[0].IsComplete);
        Assert.Equal(126.5, call.Phases[0].ObservedDurationHours);
    }

    [Fact]
    public void TimestampsComeBackAsUtc()
    {
        // A local-time read on this machine's Danish locale would move every figure in the
        // archive by an hour or two, in a document there is nothing left to check it against.
        var call = ArchiveJson.Read(ArchiveJson.Write(Sample())).PortCalls[0];

        Assert.Equal(T0, call.ArrivedUtc.ToUniversalTime());
        Assert.Equal(T0.AddHours(126.5), call.DepartedUtc.ToUniversalTime());
    }

    [Fact]
    public void ATruncatedArchiveIsRefusedByName()
    {
        // The failure the old non-empty check would have passed: a prune interrupted between
        // opening the file and flushing it leaves valid-looking JSON that simply stops.
        var json = ArchiveJson.Write(Sample());
        var truncated = json[..(json.Length / 2)];

        var refused = Assert.Throws<InvalidDataException>(
            () => ArchiveJson.Read(truncated, "port-calls-before-20260901T000000Z.json"));

        Assert.Contains("port-calls-before-20260901T000000Z.json", refused.Message);
    }

    /// <summary>
    /// An archive as the pre-versioning prune wrote it: the same field names in the same order,
    /// and no formatVersion. Copied from the anonymous type that ADR-0045's prune serialised.
    /// </summary>
    private const string BeforeVersioning = """
        {
          "archivedUtc": "2026-09-16T09:26:53Z",
          "cutoffUtc": "2026-09-05T11:57:04Z",
          "sourceFiles": ["aisdk-2026-09-01.zip"],
          "portCalls": [
            {
              "id": 94,
              "mmsi": 230687000,
              "arrivedUtc": "2026-09-01T00:00:00Z",
              "departedUtc": "2026-09-05T05:48:00Z",
              "waitingHours": 100.2,
              "workingHours": 0,
              "unclassifiedHours": 0,
              "isComplete": false,
              "portName": "Kerteminde",
              "portCountry": "DK",
              "portDistanceNm": 6.7,
              "phases": [
                {
                  "sequence": 0,
                  "phase": "Anchorage",
                  "startedUtc": "2026-09-01T00:00:00Z",
                  "endedUtc": "2026-09-04T08:43:00Z",
                  "observedDurationHours": 80.7,
                  "isComplete": false,
                  "reportedStatus": "Moored",
                  "statusAgrees": true
                }
              ]
            }
          ]
        }
        """;

    [Fact]
    public void AnArchiveWrittenBeforeVersioningStillReads()
    {
        // The first archives this code inherits were written by a prune that had no version
        // field, and the rows they describe are gone. Refusing them would mean the tool built to
        // prove archives are readable could not read the only real ones in existence.
        var document = ArchiveJson.Read(BeforeVersioning);

        Assert.Equal(ArchiveDocument.BeforeVersioning, document.FormatVersion);
        Assert.Equal(230687000, document.PortCalls[0].Mmsi);
        Assert.Equal("Kerteminde", document.PortCalls[0].PortName);
        Assert.Equal(6.7, document.PortCalls[0].PortDistanceNm);
    }

    [Fact]
    public void NothingIsLostReadingAnArchiveWrittenBeforeVersioning()
    {
        // Field for field, including the censored bound and the disagreement. The old writer
        // emitted the same names for every value this document holds; the version is the only
        // thing it lacked, which is why one of these reads completely rather than partly.
        var phase = ArchiveJson.Read(BeforeVersioning).PortCalls[0].Phases[0];

        Assert.Equal(0, phase.Sequence);
        Assert.Equal("Anchorage", phase.Phase);
        Assert.Equal(80.7, phase.ObservedDurationHours);
        Assert.False(phase.IsComplete);
        Assert.Equal("Moored", phase.ReportedStatus);
        Assert.True(phase.StatusAgrees);
        Assert.False(ArchiveJson.Read(BeforeVersioning).PortCalls[0].IsComplete);
    }

    [Fact]
    public void AnArchiveFromAnotherFormatVersionIsRefused()
    {
        var json = ArchiveJson.Write(Sample()).Replace(
            $"\"formatVersion\": {ArchiveDocument.CurrentFormatVersion}",
            "\"formatVersion\": 99",
            StringComparison.Ordinal);

        var refused = Assert.Throws<InvalidDataException>(() => ArchiveJson.Read(json));

        // Named, both of them: a reader that cannot parse this file needs to know which build can.
        Assert.Contains("99", refused.Message);
        Assert.Contains(ArchiveDocument.CurrentFormatVersion.ToString(), refused.Message);
    }

    [Fact]
    public void AnArchiveCarryingNoCallsIsRefused()
    {
        // Not a record of nothing -- a record that failed to be written. Prune declines to delete
        // on the strength of one, which is the only reason this distinction matters.
        var empty = ArchiveJson.Write(ArchiveBuilder.Build(T0, T0, [], [], []));

        Assert.Throws<InvalidDataException>(() => ArchiveJson.Read(empty));
    }

    [Fact]
    public void AMissingFieldIsRefusedRatherThanDefaulted()
    {
        // A dropped mmsi must not read back as vessel 0. Every field on the document is required,
        // so the shape is enforced by the type rather than by whoever writes the next reader.
        const string partial = """
            {
              "formatVersion": 1,
              "archivedUtc": "2026-09-01T00:00:00Z",
              "cutoffUtc": "2026-09-01T00:00:00Z",
              "sourceFiles": ["day-1.zip"],
              "portCalls": [ { "id": 1, "arrivedUtc": "2026-09-01T00:00:00Z" } ]
            }
            """;

        Assert.Throws<InvalidDataException>(() => ArchiveJson.Read(partial));
    }

    [Fact]
    public void AReformattedArchiveStillReads()
    {
        // This file is meant to be opened. Someone who reindents one, or annotates it with a
        // comment about why a call matters, should not find it unreadable afterwards.
        var clean = ArchiveJson.Write(Sample());
        var annotated = "// pruned during the September clear-down\n" + clean;

        Assert.Equal(clean, ArchiveJson.Write(ArchiveJson.Read(annotated)));
    }

    [Fact]
    public void WritesAndReadsAFileOnDisk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ais-archive-{Guid.NewGuid():N}.json");

        try
        {
            ArchiveJson.WriteFile(path, Sample());

            Assert.Equal(ArchiveJson.Write(Sample()), ArchiveJson.Write(ArchiveJson.ReadFile(path)));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
