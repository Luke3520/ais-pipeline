using AisPipeline.Adapters.Csv;
using AisPipeline.Adapters.Sqlite;
using AisPipeline.Core.Annotate;
using AisPipeline.Core.Detection;
using AisPipeline.Core.Ingest;
using AisPipeline.Core.Quality;
using AisPipeline.Core.Quality.Rules;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace AisPipeline.ApiTests;

/// <summary>
/// The API hosted in-process over a real database built from the committed fixture.
///
/// Built once for the whole class: ingesting and detecting takes long enough that doing it per
/// test would make the suite slow enough to skip, and every test here is read-only.
/// </summary>
public sealed class ApiFixture : WebApplicationFactory<Program>
{
    private readonly string _database = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), $"ais-api-{Guid.NewGuid():N}.db");

    public static string FixturePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(System.IO.Path.Combine(dir.FullName, "AisPipeline.slnx")))
        {
            dir = dir.Parent;
        }

        return System.IO.Path.Combine(dir!.FullName, "fixtures", "sample.csv");
    }

    public ApiFixture()
    {
        using (var store = new SqliteAisStore(_database))
        {
            new IngestPipeline(store, RuleRegistry.Default(),
                new IngestOptions { ShipType = "Tanker" })
                .Run(new DmaCsvSource(FixturePath()));
        }

        using (var store = new SqliteAisStore(_database))
        {
            new AnnotatePass(store,
                [new R7Teleport(), new R8CoverageGap(), new R11SpeedConsistency()]).Run();
            new DetectionPass(store).Run();
        }

    }

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.UseSetting("AIS_SQLITE", _database);

        // Pin the host to the database this fixture built. ASP.NET Core reads environment
        // variables into configuration, so an ambient AIS_POSTGRES -- which check.sh exports, and
        // CI sets for the adapter-parity suite -- would otherwise take precedence and the API
        // would serve a completely different database.
        //
        // That is not hypothetical: these tests passed locally against a populated Postgres they
        // never wrote to, and only failed in CI because its Postgres was empty. A test that reads
        // a database it did not create is asserting on someone else's data.
        builder.UseSetting("AIS_POSTGRES", string.Empty);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing)
        {
            return;
        }

        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            File.Delete(_database + suffix);
        }
    }
}
