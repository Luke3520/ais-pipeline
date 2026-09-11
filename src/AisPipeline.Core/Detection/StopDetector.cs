using AisPipeline.Core.Domain;
using AisPipeline.Core.Geo;

namespace AisPipeline.Core.Detection;

/// <summary>
/// Finds the periods a vessel spent stationary, from its fixes in time order.
///
/// Pure: takes a sequence, returns a sequence. No I/O, no clock, no database -- which is what
/// lets it be tested against hand-built cases rather than only against whatever a fixture
/// happens to contain (ADR-0003).
/// </summary>
public sealed class StopDetector
{
    private readonly DetectionThresholds _thresholds;

    public StopDetector(DetectionThresholds? thresholds = null) =>
        _thresholds = thresholds ?? DetectionThresholds.Default;

    /// <summary>
    /// Detect stops for one vessel. <paramref name="fixes"/> must be that vessel's fixes in
    /// ascending time order; the caller reads them from the store ordered by (mmsi, ts_utc).
    /// </summary>
    public IReadOnlyList<StopEvent> Detect(IReadOnlyList<PositionFix> fixes)
    {
        var stops = new List<StopEvent>();
        if (fixes.Count == 0)
        {
            return stops;
        }

        var current = new List<PositionFix>();

        // A stop is incomplete when its true start or end is unknown: it ran off the edge of
        // the ingested window, or a coverage gap hid what happened next (ADR-0011).
        var openedAtWindowEdge = false;
        var closedByGap = false;

        for (var i = 0; i < fixes.Count; i++)
        {
            var fix = fixes[i];

            if (i > 0 && (fix.TimestampUtc - fixes[i - 1].TimestampUtc) > _thresholds.MaximumGap)
            {
                // Whatever the vessel did during the silence is unknown, so a stop spanning it
                // cannot claim a duration.
                Close(stops, current, openedAtWindowEdge, complete: false);
                current.Clear();
                closedByGap = true;
            }

            var speed = fix.SpeedOverGroundKn;

            if (speed is null)
            {
                // Unknown, not stopped. Treating a missing value as zero would fabricate stops,
                // and treating it as movement would truncate real ones. It does neither: the
                // fix joins an open stop as evidence of presence but cannot start or end one.
                if (current.Count > 0)
                {
                    current.Add(fix);
                }

                continue;
            }

            if (current.Count == 0)
            {
                if (speed < _thresholds.EnterStoppedKn)
                {
                    // A stop already under way at the first fix has an unknown true start.
                    openedAtWindowEdge = i == 0 || closedByGap;
                    current.Add(fix);
                }

                closedByGap = false;
                continue;
            }

            if (speed >= _thresholds.LeaveStoppedKn)
            {
                Close(stops, current, openedAtWindowEdge, complete: !openedAtWindowEdge);
                current.Clear();
                openedAtWindowEdge = false;
                continue;
            }

            // Between the two thresholds the state does not change. That gap is the hysteresis.
            current.Add(fix);
        }

        // Still stopped when the data ran out: the end is unknown.
        Close(stops, current, openedAtWindowEdge, complete: false);

        return stops;
    }

    private void Close(List<StopEvent> stops, List<PositionFix> fixes, bool openAtEdge, bool complete)
    {
        if (fixes.Count < 2)
        {
            return;
        }

        var duration = fixes[^1].TimestampUtc - fixes[0].TimestampUtc;
        if (duration < _thresholds.MinimumStopDuration)
        {
            return;
        }

        // Geometry uses only fixes no rule found positionally unreliable. One corrupt position
        // drags the centroid AND supplies the maximum, so a single bad fix produced 2,387 nm of
        // drift across hundreds of good ones (ADR-0021). Excluded fixes still count toward
        // FixCount and still bound the stop in time -- they are evidence the vessel was there,
        // just not evidence of where.
        var reliable = fixes.Where(f => !f.PositionUnreliable).ToList();
        if (reliable.Count == 0)
        {
            return;
        }

        var centroidLat = reliable.Average(f => f.Latitude);
        var centroidLon = reliable.Average(f => f.Longitude);
        var maxDrift = reliable.Max(f =>
            Haversine.DistanceNm(centroidLat, centroidLon, f.Latitude, f.Longitude));

        var reported = ModalStatus(fixes);

        stops.Add(new StopEvent
        {
            Mmsi = fixes[0].Mmsi,
            StartedUtc = fixes[0].TimestampUtc,
            EndedUtc = fixes[^1].TimestampUtc,
            CentroidLatitude = centroidLat,
            CentroidLongitude = centroidLon,
            MaxDriftNm = maxDrift,
            FixCount = fixes.Count,
            ReportedStatus = reported,

            // R10. The speed said stationary; if the vessel's own status said otherwise, that
            // disagreement is the finding and is recorded rather than resolved.
            StatusAgrees = NavigationalStatus.Classify(reported) != ReportedActivity.UnderWay,

            IsComplete = complete && !openAtEdge,
            FirstPositionId = fixes[0].Id,
            LastPositionId = fixes[^1].Id,
        });
    }

    private static string? ModalStatus(List<PositionFix> fixes) => fixes
        .Select(f => f.NavigationalStatus)
        .Where(s => !string.IsNullOrEmpty(s))
        .GroupBy(s => s, StringComparer.Ordinal)
        .OrderByDescending(g => g.Count())
        .ThenBy(g => g.Key, StringComparer.Ordinal)
        .FirstOrDefault()?.Key;
}
