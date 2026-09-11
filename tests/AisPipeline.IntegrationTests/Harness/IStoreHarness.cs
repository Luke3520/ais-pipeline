using AisPipeline.Core.Ports;

namespace AisPipeline.IntegrationTests.Harness;

/// <summary>
/// An isolated store to run a test against, plus the few raw queries a test needs to check what
/// actually landed.
///
/// Exists so one suite runs unchanged against every adapter. The ports promise that SQLite and
/// Postgres are interchangeable (ADR-0004); a suite that only ever ran against one would be
/// taking that promise on trust.
/// </summary>
public interface IStoreHarness : IDisposable
{
    /// <summary>Adapter name, so a failure says which engine it came from.</summary>
    string Name { get; }

    /// <summary>Open a store over this harness's isolated database. Called more than once per test.</summary>
    IAisStore Create();

    /// <summary>Row count for a table.</summary>
    long Count(string table);

    /// <summary>Rows matching a WHERE clause, for the provenance assertions.</summary>
    long CountWhere(string sql);

    /// <summary>Single numeric value, for assertions about which row won a conflict.</summary>
    long Scalar(string sql);
}
