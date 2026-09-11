using AisPipeline.Core.Ports;
using AisPipeline.Core.Query;
using GreenDonut;

namespace AisPipeline.Api.GraphQL;

/// <summary>
/// Vessels, one query per request rather than one per port call.
///
/// This is the N+1 the analytical surface would otherwise have: a page of 100 port calls, each
/// resolving <c>vessel</c>, is 100 round trips returning at most a few hundred rows. DataLoader
/// collects the keys raised during one execution and asks once.
///
/// The reason this is possible at all is that <see cref="IAisQueries.GetVessels"/> exists in
/// batched form. A resolver cannot batch what the port cannot express (ADR-0027).
/// </summary>
public sealed class VesselByMmsiDataLoader : BatchDataLoader<long, StoredVessel?>
{
    private readonly IAisQueries _queries;

    public VesselByMmsiDataLoader(
        IAisQueries queries, IBatchScheduler scheduler, DataLoaderOptions options)
        : base(scheduler, options) => _queries = queries;

    protected override Task<IReadOnlyDictionary<long, StoredVessel?>> LoadBatchAsync(
        IReadOnlyList<long> keys, CancellationToken cancellationToken)
    {
        // A key with no vessel is absent from the result, which DataLoader surfaces as null.
        // Inventing a placeholder would be worse: the client could not tell "no such vessel"
        // from "a vessel with nothing known about it", and R9 means the second really happens.
        IReadOnlyDictionary<long, StoredVessel?> byMmsi = _queries
            .GetVessels(keys)
            .ToDictionary(v => v.Mmsi, v => (StoredVessel?)v);

        return Task.FromResult(byMmsi);
    }
}

/// <summary>
/// A vessel's port calls, batched across every vessel in one execution, most recent first.
///
/// The cap bounds the work one query can demand. It is paired with
/// <see cref="PortCallCountByVesselDataLoader"/> so a client can always tell a vessel that made
/// exactly <see cref="PerVesselCap"/> calls from one whose list was cut short -- a truncation
/// nobody can detect is a wrong number, not a limit (ADR-0028).
/// </summary>
public sealed class PortCallsByVesselDataLoader : GroupedDataLoader<long, StoredPortCall>
{
    /// <summary>Exposed so the schema description can state the ceiling rather than imply none.</summary>
    public const int PerVesselCap = 50;

    private readonly IAisQueries _queries;

    public PortCallsByVesselDataLoader(
        IAisQueries queries, IBatchScheduler scheduler, DataLoaderOptions options)
        : base(scheduler, options) => _queries = queries;

    protected override Task<ILookup<long, StoredPortCall>> LoadGroupedBatchAsync(
        IReadOnlyList<long> keys, CancellationToken cancellationToken) =>
        Task.FromResult(_queries
            .GetPortCallsForVessels(keys, PerVesselCap)
            .ToLookup(p => p.Mmsi));
}

/// <summary>
/// A port call's phases with their stops, batched across every port call in one execution.
///
/// The second level of the same N+1: without this, resolving phases for 100 port calls is 100
/// queries, and resolving each phase's stop would be another one per phase.
/// </summary>
public sealed class PhasesByPortCallDataLoader : GroupedDataLoader<long, PhaseWithStop>
{
    private readonly IAisQueries _queries;

    public PhasesByPortCallDataLoader(
        IAisQueries queries, IBatchScheduler scheduler, DataLoaderOptions options)
        : base(scheduler, options) => _queries = queries;

    protected override Task<ILookup<long, PhaseWithStop>> LoadGroupedBatchAsync(
        IReadOnlyList<long> keys, CancellationToken cancellationToken) =>
        Task.FromResult(_queries
            .GetPhasesForPortCalls(keys)
            .Select(row => new PhaseWithStop(row.Phase, row.Stop))
            .ToLookup(p => p.Phase.PortCallId));
}

/// <summary>
/// How many port calls each vessel really has, so a capped list is detectable.
/// </summary>
public sealed class PortCallCountByVesselDataLoader : BatchDataLoader<long, long>
{
    private readonly IAisQueries _queries;

    public PortCallCountByVesselDataLoader(
        IAisQueries queries, IBatchScheduler scheduler, DataLoaderOptions options)
        : base(scheduler, options) => _queries = queries;

    protected override Task<IReadOnlyDictionary<long, long>> LoadBatchAsync(
        IReadOnlyList<long> keys, CancellationToken cancellationToken) =>
        Task.FromResult(_queries.CountPortCallsForVessels(keys));
}

/// <summary>A phase and the stop it refers to, already joined.</summary>
public sealed record PhaseWithStop(StoredPhase Phase, StoredStop Stop);
