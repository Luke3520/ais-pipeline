using AisPipeline.Core.Domain;

namespace AisPipeline.Core.Quality.Rules;

/// <summary>
/// R4 -- the position is not a position: DMA's unavailable-position sentinel, null island, or
/// a coordinate outside the valid range.
///
/// The sentinel is latitude 91 paired with longitude 0 -- NOT longitude 181, which the AIS
/// specification implies and which never appears in these files. A longitude-181 test catches
/// nothing. Measured at 7,013 rows (0.41%) across a 1.7M-row sample.
/// </summary>
public sealed class R4PositionSentinel : IRecordRule
{
    /// <summary>DMA's unavailable-latitude sentinel.</summary>
    public const double LatitudeSentinel = 91.0;

    public const double MaxLatitude = 90.0;
    public const double MaxLongitude = 180.0;

    public string Id => RuleIds.PositionSentinel;

    public string Description => "Position sentinel, null island, or coordinate out of range";

    public RuleHit? Evaluate(RawAisRecord record)
    {
        if (record.Latitude == LatitudeSentinel)
        {
            return Reject($"latitude sentinel {LatitudeSentinel} (lon {record.Longitude})");
        }

        if (record.Latitude == 0.0 && record.Longitude == 0.0)
        {
            return Reject("null island (0,0)");
        }

        if (Math.Abs(record.Latitude) > MaxLatitude)
        {
            return Reject($"latitude {record.Latitude} out of range");
        }

        if (Math.Abs(record.Longitude) > MaxLongitude)
        {
            return Reject($"longitude {record.Longitude} out of range");
        }

        return null;
    }

    private RuleHit Reject(string detail) => new(Id, RuleAction.Reject, detail);
}
