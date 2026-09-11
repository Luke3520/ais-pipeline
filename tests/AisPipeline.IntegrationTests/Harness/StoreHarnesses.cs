using System.Collections;

namespace AisPipeline.IntegrationTests.Harness;

/// <summary>
/// The adapters a test runs against.
///
/// Postgres is included only when <c>AIS_POSTGRES</c> is set, so a developer without Docker can
/// still run the suite. That is a real risk of the suite quietly shrinking, which is why
/// <see cref="AdapterCoverageTests"/> fails in CI if Postgres was skipped there.
/// </summary>
public sealed class StoreHarnesses : IEnumerable<object[]>
{
    public IEnumerator<object[]> GetEnumerator()
    {
        yield return [new Func<IStoreHarness>(() => new SqliteHarness())];

        if (PostgresHarness.ConnectionString is { } connectionString)
        {
            yield return [new Func<IStoreHarness>(() => new PostgresHarness(connectionString))];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
