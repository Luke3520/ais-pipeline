using System.Globalization;

namespace AisPipeline.Core.Radar;

/// <summary>What a cell shows. The CLI chooses colours; Core only says what each cell is.</summary>
public enum Ink
{
    Sea,
    Ring,
    Coast,
    Beam,
    Trail,
    Moving,
    Stopped,
    Conflict,
    Label,
}

/// <summary>One character on the scope. <paramref name="Lit"/> is true just behind the beam.</summary>
public readonly record struct Cell(char Glyph, Ink Ink, bool Lit);

/// <summary>A rendered scope: cells, plus the range one ring represents so the CLI can say so.</summary>
public sealed class RadarFrame
{
    public RadarFrame(Cell[,] cells, double ringSpacingNm)
    {
        Cells = cells;
        RingSpacingNm = ringSpacingNm;
    }

    public Cell[,] Cells { get; }
    public int Rows => Cells.GetLength(0);
    public int Columns => Cells.GetLength(1);
    public double RingSpacingNm { get; }

    /// <summary>The frame as plain text, no colour. What the golden-frame test compares.</summary>
    public IReadOnlyList<string> Text()
    {
        var lines = new string[Rows];
        for (var r = 0; r < Rows; r++)
        {
            var chars = new char[Columns];
            for (var c = 0; c < Columns; c++)
            {
                chars[c] = Cells[r, c].Glyph;
            }

            lines[r] = new string(chars);
        }

        return lines;
    }
}

/// <summary>Everything a frame is drawn from.</summary>
public sealed record RadarScene(
    GeoBox Region,
    int Columns,
    int Rows,
    IReadOnlyList<IReadOnlyList<(double LatDeg, double LonDeg)>> Coastline,
    IReadOnlyList<VesselOnScope> Vessels,
    double SweepDeg,
    IReadOnlyList<long> Labelled);

/// <summary>
/// Draws a scene. Pure: the same scene always produces the same frame, which is what lets a
/// golden-frame test pin it.
///
/// Layers are drawn as separate braille canvases and then composited per cell, lowest first:
/// rings, coast, beam, trails, then the vessels and their names as ordinary characters on top.
/// Separate canvases because braille dots only combine within a cell, and a coastline dot sharing a
/// cell with a trail dot would otherwise lose its colour to whichever was drawn last.
/// </summary>
public static class RadarFrameBuilder
{
    /// <summary>Range ring spacings to choose from: round numbers a chart would use.</summary>
    private static readonly double[] NiceRingSpacingsNm = [1, 2, 5, 10, 15, 20, 25, 50, 100];

    public static RadarFrame Build(RadarScene scene)
    {
        var rings = new BrailleCanvas(scene.Columns, scene.Rows);
        var coast = new BrailleCanvas(scene.Columns, scene.Rows);
        var beam = new BrailleCanvas(scene.Columns, scene.Rows);
        var trails = new BrailleCanvas(scene.Columns, scene.Rows);
        var projection = new RadarProjection(scene.Region, coast.WidthDots, coast.HeightDots);

        double cx = coast.WidthDots / 2.0, cy = coast.HeightDots / 2.0;
        var maxRadiusDots = Math.Min(cx, cy);
        var ringSpacingNm = RingSpacing(projection.DotsToNm(maxRadiusDots));
        for (var i = 1; i <= RadarThresholds.RangeRings; i++)
        {
            rings.Circle(cx, cy, projection.NmToDots(ringSpacingNm * i));
        }

        foreach (var line in scene.Coastline)
        {
            for (var i = 1; i < line.Count; i++)
            {
                var (x0, y0) = projection.Project(line[i - 1].LatDeg, line[i - 1].LonDeg);
                var (x1, y1) = projection.Project(line[i].LatDeg, line[i].LonDeg);
                coast.Line(x0, y0, x1, y1);
            }
        }

        var sweepRad = scene.SweepDeg * Math.PI / 180.0;
        var reach = Math.Sqrt(cx * cx + cy * cy);
        beam.Line(cx, cy, cx + reach * Math.Sin(sweepRad), cy - reach * Math.Cos(sweepRad));

        foreach (var v in scene.Vessels)
        {
            for (var i = 1; i < v.Trail.Count; i++)
            {
                var (x0, y0) = projection.Project(v.Trail[i - 1].LatDeg, v.Trail[i - 1].LonDeg);
                var (x1, y1) = projection.Project(v.Trail[i].LatDeg, v.Trail[i].LonDeg);
                trails.Line(x0, y0, x1, y1);
            }
        }

        var cells = new Cell[scene.Rows, scene.Columns];
        for (var r = 0; r < scene.Rows; r++)
        {
            for (var c = 0; c < scene.Columns; c++)
            {
                var lit = Lit(c, r, scene);
                cells[r, c] =
                    !trails.IsEmpty(c, r) ? new Cell(trails.CharAt(c, r), Ink.Trail, lit)
                    : !beam.IsEmpty(c, r) ? new Cell(beam.CharAt(c, r), Ink.Beam, true)
                    : !coast.IsEmpty(c, r) ? new Cell(coast.CharAt(c, r), Ink.Coast, lit)
                    : !rings.IsEmpty(c, r) ? new Cell(rings.CharAt(c, r), Ink.Ring, lit)
                    : new Cell(' ', Ink.Sea, lit);
            }
        }

        var placed = new Dictionary<long, (int Col, int Row)>();
        foreach (var v in scene.Vessels)
        {
            var (x, y) = projection.Project(v.LatitudeDeg, v.LongitudeDeg);
            if (!projection.OnCanvas(x, y))
            {
                continue;
            }

            int col = (int)(x / 2), row = (int)(y / 4);
            cells[row, col] = new Cell(Glyph(v), InkOf(v.State), Lit(col, row, scene));
            placed[v.Mmsi] = (col, row);
        }

        foreach (var mmsi in scene.Labelled.Take(RadarThresholds.MaxLabels))
        {
            if (placed.TryGetValue(mmsi, out var at))
            {
                var name = scene.Vessels.First(v => v.Mmsi == mmsi).Name;
                Label(cells, at.Col + 2, at.Row, name);
            }
        }

        var ringLabel = string.Create(CultureInfo.InvariantCulture, $"{ringSpacingNm:0.#} nm");
        var ringRow = (int)((cy - projection.NmToDots(ringSpacingNm)) / 4);
        Label(cells, (int)(cx / 2) + 1, ringRow, ringLabel, ink: Ink.Ring);

        return new RadarFrame(cells, ringSpacingNm);
    }

    /// <summary>
    /// The arrow a moving vessel is drawn with, from its reported course over ground to the nearest
    /// of eight points. No course, no arrow: a plus means "moving, heading unknown" rather than an
    /// arrow pointing a direction nobody reported.
    /// </summary>
    public static char Glyph(VesselOnScope v) => v.State switch
    {
        ScopeState.Stopped => 'o',
        ScopeState.StoppedClaimingUnderWay or ScopeState.MovingClaimingStationary => '!',
        _ => v.CourseDeg is { } cog
            ? "↑↗→↘↓↙←↖"[(int)Math.Round(((cog % 360) + 360) % 360 / 45.0) % 8]
            : '+',
    };

    private static Ink InkOf(ScopeState s) => s switch
    {
        ScopeState.Moving => Ink.Moving,
        ScopeState.Stopped => Ink.Stopped,
        _ => Ink.Conflict,
    };

    public static double RingSpacing(double maxRadiusNm)
    {
        var wanted = maxRadiusNm / RadarThresholds.RangeRings;
        var best = NiceRingSpacingsNm[0];
        foreach (var s in NiceRingSpacingsNm)
        {
            if (s <= wanted)
            {
                best = s;
            }
        }

        return best;
    }

    /// <summary>
    /// True when a cell lies within the afterglow behind the beam. Bearings are measured clockwise
    /// from north, like the beam, with a cell counted twice as tall as it is wide.
    /// </summary>
    private static bool Lit(int col, int row, RadarScene scene)
    {
        double dx = col + 0.5 - scene.Columns / 2.0;
        double dy = (scene.Rows / 2.0 - (row + 0.5)) * 2.0;
        var bearingDeg = (Math.Atan2(dx, dy) * 180.0 / Math.PI + 360.0) % 360.0;
        var behindDeg = ((scene.SweepDeg - bearingDeg) % 360.0 + 360.0) % 360.0;
        return behindDeg < RadarThresholds.AfterglowDeg;
    }

    /// <summary>Write text into free cells only: never over a vessel or another label.</summary>
    private static void Label(Cell[,] cells, int col, int row, string text, Ink ink = Ink.Label)
    {
        if (row < 0 || row >= cells.GetLength(0))
        {
            return;
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = col + i;
            if (c < 0 || c >= cells.GetLength(1) || cells[row, c].Ink is Ink.Moving or Ink.Stopped or Ink.Conflict or Ink.Label)
            {
                return;
            }
        }

        for (var i = 0; i < text.Length; i++)
        {
            cells[row, col + i] = new Cell(text[i], ink, false);
        }
    }
}
