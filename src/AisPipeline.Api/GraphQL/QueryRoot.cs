using AisPipeline.Core.Ports;
using AisPipeline.Core.Query;

namespace AisPipeline.Api.GraphQL;

/// <summary>
/// The analytical read model.
///
/// GraphQL earns its place here rather than duplicating REST: this data is a graph of nested
/// aggregates whose clients want wildly different slices of it — vessel → port calls → phases →
/// stops → fixes. Over REST that is either over-fetching or a growth of ad-hoc `include`
/// parameters. The operational surface stays REST (ADR-0027).
/// </summary>
public sealed class QueryRoot
{
    /// <summary>One vessel by MMSI.</summary>
    public StoredVessel? Vessel([Service] IAisQueries queries, long mmsi) => queries.GetVessel(mmsi);

    /// <summary>Vessels, optionally filtered by resolved ship type.</summary>
    public IReadOnlyList<StoredVessel> Vessels(
        [Service] IAisQueries queries, string? shipType = null, int limit = 50) =>
        queries.ListVessels(shipType, limit);

    /// <summary>Port calls with their waiting and working split.</summary>
    public IReadOnlyList<StoredPortCall> PortCalls(
        [Service] IAisQueries queries,
        long? mmsi = null,
        double? minWaitingHours = null,
        bool completeOnly = false,
        int limit = 50) =>
        queries.ListPortCalls(new PortCallFilter
        {
            Mmsi = mmsi,
            MinWaitingHours = minWaitingHours,
            CompleteOnly = completeOnly,
            Limit = limit,
        });

    /// <summary>Stops. <c>disagreementsOnly</c> selects the rule R10 conflicts.</summary>
    public IReadOnlyList<StoredStop> Stops(
        [Service] IAisQueries queries,
        long? mmsi = null,
        double? minHours = null,
        bool completeOnly = false,
        bool disagreementsOnly = false,
        int limit = 50) =>
        queries.ListStops(new StopFilter
        {
            Mmsi = mmsi,
            MinHours = minHours,
            CompleteOnly = completeOnly,
            DisagreementsOnly = disagreementsOnly,
            Limit = limit,
        });

    /// <summary>What the pipeline refused, and under which rule.</summary>
    public IReadOnlyList<RuleHitCount> Quality([Service] IAisQueries queries) => queries.QualityReport();

    /// <summary>Ingest runs and their counters.</summary>
    public IReadOnlyList<StoredRun> Runs([Service] IAisQueries queries) => queries.ListRuns();
}

/// <summary>Fields hanging off a vessel, resolved through DataLoader.</summary>
[ExtendObjectType(typeof(StoredVessel))]
public sealed class VesselExtensions
{
    /// <summary>
    /// This vessel's port calls, most recent first, capped at 50. Compare the length against
    /// <c>portCallCount</c> to tell whether the list was cut short.
    /// </summary>
    public async Task<IReadOnlyList<StoredPortCall>> PortCalls(
        [Parent] StoredVessel vessel,
        PortCallsByVesselDataLoader loader,
        CancellationToken cancellationToken) =>
        await loader.LoadRequiredAsync(vessel.Mmsi, cancellationToken);

    /// <summary>
    /// How many port calls this vessel has in total, ignoring the cap on <c>portCalls</c>.
    ///
    /// Without this a client cannot distinguish a vessel that made exactly fifty calls from one
    /// whose list was truncated, and any sum of waiting or working hours over the returned list
    /// would silently undercount (ADR-0028).
    /// </summary>
    public async Task<long> PortCallCount(
        [Parent] StoredVessel vessel,
        PortCallCountByVesselDataLoader loader,
        CancellationToken cancellationToken) =>
        await loader.LoadAsync(vessel.Mmsi, cancellationToken);
}

/// <summary>Fields hanging off a port call, resolved through DataLoader.</summary>
[ExtendObjectType(typeof(StoredPortCall))]
public sealed class PortCallExtensions
{
    /// <summary>
    /// The vessel that made this call.
    ///
    /// The textbook N+1: a page of port calls each resolving its vessel. Batched, it is one query
    /// however many calls are on the page, and <c>GraphQLNPlusOneTests</c> asserts the count
    /// rather than trusting the wiring.
    /// </summary>
    public async Task<StoredVessel?> Vessel(
        [Parent] StoredPortCall portCall,
        VesselByMmsiDataLoader loader,
        CancellationToken cancellationToken) =>
        await loader.LoadAsync(portCall.Mmsi, cancellationToken);

    /// <summary>Berth and anchorage phases in order, each with its stop.</summary>
    public async Task<IReadOnlyList<PhaseWithStop>> Phases(
        [Parent] StoredPortCall portCall,
        PhasesByPortCallDataLoader loader,
        CancellationToken cancellationToken) =>
        await loader.LoadRequiredAsync(portCall.Id, cancellationToken);
}
