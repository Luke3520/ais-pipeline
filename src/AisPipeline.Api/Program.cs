using AisPipeline.Adapters.Sql;
using AisPipeline.Api;
using AisPipeline.Api.GraphQL;
using AisPipeline.Core.Ports;
using AisPipeline.Core.Benchmarks;
using AisPipeline.Core.Laytime;
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
// Laytime for one port call, with the charter party terms supplied as query parameters.
//
// A GET, and deliberately so. The terms are a handful of numbers and a timestamp, which fit in a
// URL; the Statement of Facts does not, and accepting one would be the write surface ADR-0029
// names as its second trigger -- so `reconcile` stays a CLI verb and this endpoint prices only
// what AIS can see on its own (ADR-0042).
app.MapGet("/portcalls/{id:long}/laytime", (
        IAisQueries q,
        long id,
        double allowedHours = CharterPartyDefaults.AllowedHours,
        double ratePerDay = CharterPartyDefaults.RatePerDay,
        double turnHours = CharterPartyDefaults.TurnHours,
        string? currency = null,
        DateTime? norUtc = null) =>
    {
        if (q.GetPortCall(id) is not { } call)
        {
            return Results.NotFound(new { error = $"no port call with id {id}" });
        }

        if (allowedHours < 0 || ratePerDay < 0 || turnHours < 0)
        {
            return Results.BadRequest(new
            {
                error = "allowedHours, ratePerDay and turnHours cannot be negative",
            });
        }

        // AIS cannot observe a notice of readiness -- no transponder emits one -- so when the
        // caller supplies none, arrival is substituted and the response says the figure rests on
        // an assumption (ADR-0030).
        var assumed = norUtc is null;
        var nor = norUtc is { } given
            ? DateTime.SpecifyKind(given, DateTimeKind.Utc)
            : call.ArrivedUtc;

        var terms = new CharterPartyTerms
        {
            LaytimeAllowedHours = allowedHours,
            DemurrageRatePerDay = Money.FromMajor((decimal)ratePerDay, currency ?? CharterPartyDefaults.Currency),
            NoticeOfReadinessUtc = nor,
            NoticeOfReadinessIsAssumed = assumed,
            TurnTimeHours = turnHours,
        };

        var assessment = LaytimeAssessor.Assess(
            PortCallRebuilder.FromStored(call, q.GetPhasesForPortCalls([call.Id])),
            terms);

        if (!assessment.IsPriced)
        {
            // 422 rather than 400: the request is well formed and the call exists. What is missing
            // is evidence, and the body says which -- a refusal nobody can read is indistinguishable
            // from a server fault.
            return Results.UnprocessableEntity(new
            {
                portCallId = call.Id,
                priced = false,
                refusal = assessment.Refusal.ToString(),
                detail = assessment.RefusalDetail,
            });
        }

        var statement = assessment.Statement!;

        return Results.Ok(new
        {
            portCallId = call.Id,
            call.Mmsi,
            port = call.PortName,
            portDistanceNm = call.PortDistanceNm,
            call.ArrivedUtc,
            call.DepartedUtc,
            call.IsComplete,
            priced = true,
            noticeOfReadinessUtc = nor,
            noticeOfReadinessIsAssumed = assumed,
            commencedUtc = statement.CommencedUtc,
            statement.CommencementReason,
            completedUtc = statement.CompletedUtc,
            statement.AllowedHours,
            statement.UsedHours,
            statement.ExceptedHours,
            statement.DemurrageHours,
            statement.HoursSaved,
            demurrageOwed = statement.DemurrageOwed.ToString(),
            demurrageOwedMinorUnits = statement.DemurrageOwed.MinorUnits,

            // Every hour between commencement and completion belongs to exactly one line. A total
            // nobody can decompose is a total nobody can dispute, and disputing it is the point.
            lines = statement.Lines.Select(l => new
            {
                l.FromUtc,
                l.ToUtc,
                hours = l.Hours,
                kind = l.Kind.ToString(),
                l.Reason,
            }),
        });
    })
   .WithSummary("Laytime and demurrage for one port call, on supplied charter party terms");

// Port benchmarks: what calls at a port actually took.
//
// Every figure ships with the count behind it and with what was thrown away to produce it. On the
// current window 357 attributed calls become 119 usable ones, and a median drawn from a third of
// the data without saying so is worse than no median (ADR-0043).
app.MapGet("/ports", (IAisQueries q) =>
        Results.Ok(PortBenchmarkBuilder.Build(q.PortCallHoursForBenchmarks())
            .Select(b => new
            {
                b.WpiNumber,
                b.PortName,
                b.Country,
                b.AttributedCalls,
                b.UsableCalls,
                b.ExcludedIncomplete,
                b.ExcludedTooFar,
                b.MedianWaitingHours,
                b.P90WaitingHours,
                b.MedianWorkingHours,
            })))
   .WithSummary("Waiting and working benchmarks per port, with the sample each rests on");

// The question an agent actually asks is not "what is the median" but "was mine unusual".
app.MapGet("/ports/{wpiNumber:int}", (IAisQueries q, int wpiNumber, double? waitingHours) =>
    {
        var benchmark = PortBenchmarkBuilder.Build(q.PortCallHoursForBenchmarks())
            .FirstOrDefault(b => b.WpiNumber == wpiNumber);

        if (benchmark is null)
        {
            return Results.NotFound(new { error = $"no calls attributed to port {wpiNumber}" });
        }

        return Results.Ok(new
        {
            benchmark.WpiNumber,
            benchmark.PortName,
            benchmark.Country,
            benchmark.AttributedCalls,
            benchmark.UsableCalls,
            benchmark.ExcludedIncomplete,
            benchmark.ExcludedTooFar,
            benchmark.MedianWaitingHours,
            benchmark.P90WaitingHours,
            benchmark.MedianWorkingHours,

            // Published so the percentiles can be checked rather than trusted.
            waitingHoursObserved = benchmark.WaitingHours,

            // Null when the sample is too small to rank against, never a confident-looking zero.
            comparison = waitingHours is { } hours
                ? new
                {
                    hours,
                    percentile = benchmark.RankWaiting(hours),
                    longerThanCalls = benchmark.WaitingHours.Count(h => h <= hours),
                    ofCalls = benchmark.UsableCalls,
                }
                : null,
        });
    })
   .WithSummary("One port's benchmark, optionally ranking a given wait against it");

app.MapGet("/quality", (IAisQueries q) =>
        Results.Ok(QualityReport.Build(RuleRegistry.Default(), q.QualityReport())))
   .WithSummary("Every rule, what it rejected, and what it flagged");

app.MapGet("/runs", (IAisQueries q) => Results.Ok(q.ListRuns()))
   .WithSummary("Ingest runs and their counters");

app.MapGraphQL();

app.Run();

/// <summary>Exposed so WebApplicationFactory can host this app in tests.</summary>
public partial class Program;
