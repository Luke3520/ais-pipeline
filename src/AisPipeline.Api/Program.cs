using AisPipeline.Adapters.Sql;
using AisPipeline.Api;
using AisPipeline.Api.GraphQL;
using AisPipeline.Core.Ports;
using AisPipeline.Core.Quality;
using AisPipeline.Core.Query;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

var storeOptions = StoreOptions.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(storeOptions);

// Scoped: one query object per request, so its QueryCount measures that request alone. That is
// what makes the N+1 assertion possible (ADR-0027).
builder.Services.AddScoped<SqlAisQueries>(sp =>
{
    var options = sp.GetRequiredService<StoreOptions>();
    return new SqlAisQueries(options.ConnectionFactory, options.Dialect);
});
builder.Services.AddScoped<IAisQueries>(sp => sp.GetRequiredService<SqlAisQueries>());

builder.Services.AddOpenApi();

builder.Services
    .AddGraphQLServer()
    .AddQueryType<QueryRoot>()
    .AddTypeExtension<VesselExtensions>()
    .AddTypeExtension<PortCallExtensions>()
    .AddDataLoader<VesselByMmsiDataLoader>()
    .AddDataLoader<PortCallsByVesselDataLoader>()
    .AddDataLoader<PortCallCountByVesselDataLoader>()
    .AddDataLoader<PhasesByPortCallDataLoader>()
    // The whole point of the analytical surface is nesting, so depth has to be allowed -- but an
    // unbounded depth is a denial of service, since each level multiplies the work.
    .ModifyPagingOptions(o => o.MaxPageSize = 200)
    .AddMaxExecutionDepthRule(10);

var app = builder.Build();

app.MapOpenApi();
app.MapScalarApiReference();

// The showcase page, served from this application's own wwwroot.
//
// Same-origin deliberately: ADR-0029 listed CORS among the costs of a browser UI, and that is a cost
// of a SEPARATELY hosted client. Serving the files from here removes the requirement rather than
// configuring it, and a cross-origin dev server is the thing that would bring it back.
//
// Read-only, and over the derived layer only -- no positions reach a browser, which is what keeps
// the read path unchanged (ADR-0037).
app.UseDefaultFiles();
app.UseStaticFiles();

// --- REST: the operational surface. Resource-shaped, stable, cheap to cache. -----------------
// Anything exploratory and deeply nested belongs in GraphQL instead; the two are not duplicated
// over the same shapes (ADR-0027).

app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
   .WithSummary("Liveness check");

app.MapGet("/vessels", (IAisQueries q, string? shipType, int limit = 100) =>
        Results.Ok(q.ListVessels(shipType, limit)))
   .WithSummary("Vessels, optionally filtered by resolved ship type");

app.MapGet("/vessels/{mmsi:long}", (IAisQueries q, long mmsi) =>
        q.GetVessel(mmsi) is { } vessel ? Results.Ok(vessel) : Results.NotFound())
   .WithSummary("One vessel by MMSI");

app.MapGet("/vessels/{mmsi:long}/stops", (IAisQueries q, long mmsi, bool completeOnly = false,
        double? minHours = null, int limit = 100) =>
        Results.Ok(q.ListStops(new StopFilter
        {
            Mmsi = mmsi,
            CompleteOnly = completeOnly,
            MinHours = minHours,
            Limit = limit,
        })))
   .WithSummary("Stops for one vessel. duration_hours is only meaningful when is_complete");

app.MapGet("/stops", (IAisQueries q, double? minHours, bool completeOnly = false,
        bool disagreementsOnly = false, int limit = 100) =>
        Results.Ok(q.ListStops(new StopFilter
        {
            MinHours = minHours,
            CompleteOnly = completeOnly,
            DisagreementsOnly = disagreementsOnly,
            Limit = limit,
        })))
   .WithSummary("Stops across all vessels. disagreementsOnly selects rule R10 conflicts");

app.MapGet("/portcalls", (IAisQueries q, long? mmsi, double? minWaitingHours,
        bool completeOnly = false, int limit = 100) =>
        Results.Ok(q.ListPortCalls(new PortCallFilter
        {
            Mmsi = mmsi,
            MinWaitingHours = minWaitingHours,
            CompleteOnly = completeOnly,
            Limit = limit,
        })))
   .WithSummary("Port calls with waiting and working hours");

// Reconciled against the registry, not served raw. The store only knows the rules that left a
// mark, so a raw count list omits every rule that never fired -- and a client cannot tell a check
// that passed from a check that was never registered (ADR-0032).
app.MapGet("/quality", (IAisQueries q) =>
        Results.Ok(QualityReport.Build(RuleRegistry.Default(), q.QualityReport())))
   .WithSummary("Every rule, what it rejected, and what it flagged");

app.MapGet("/runs", (IAisQueries q) => Results.Ok(q.ListRuns()))
   .WithSummary("Ingest runs and their counters");

app.MapGraphQL();

app.Run();

/// <summary>Exposed so WebApplicationFactory can host this app in tests.</summary>
public partial class Program;
