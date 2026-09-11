using AisPipeline.IntegrationTests.Harness;

namespace AisPipeline.IntegrationTests;

/// <summary>
/// Guards the guard.
///
/// The store suite silently drops Postgres when <c>AIS_POSTGRES</c> is unset, so a developer
/// without Docker can still run it. That convenience is also the way adapter parity quietly stops
/// being tested: the suite would keep passing while covering half of what it claims to.
///
/// The same reasoning as failing on zero discovered tests -- a check that skips looks exactly like
/// a check that passed (docs/rules/checks-and-review.md).
/// </summary>
public class AdapterCoverageTests
{
    [Fact]
    public void PostgresIsExercisedWhereverSkippingItWouldBeDishonest()
    {
        var isCi = Environment.GetEnvironmentVariable("CI") == "true"
            || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";

        if (!isCi)
        {
            // Locally, skipping is a deliberate convenience: `docker compose up -d` and set
            // AIS_POSTGRES to include it.
            return;
        }

        Assert.True(
            PostgresHarness.IsAvailable,
            $"CI ran without {PostgresHarness.EnvironmentVariable} set, so every Postgres case " +
            "was skipped and adapter parity went untested. Fix the workflow's service container " +
            "rather than this assertion.");
    }

    [Fact]
    public void TheSuiteCoversEveryAdapterItKnowsAbout()
    {
        var harnesses = new StoreHarnesses().Select(row => ((Func<IStoreHarness>)row[0])()).ToList();

        try
        {
            var names = harnesses.Select(h => h.Name).ToList();

            Assert.Contains("SQLite", names);

            if (PostgresHarness.IsAvailable)
            {
                Assert.Contains("Postgres", names);
            }
        }
        finally
        {
            foreach (var harness in harnesses)
            {
                harness.Dispose();
            }
        }
    }
}
