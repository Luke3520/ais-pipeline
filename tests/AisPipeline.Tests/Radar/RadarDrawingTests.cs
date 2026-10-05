using AisPipeline.Core.Domain;
using AisPipeline.Core.Radar;

namespace AisPipeline.Tests.Radar;

public class BrailleCanvasTests
{
    [Theory]
    [InlineData(0, 0, '⠁')]
    [InlineData(0, 1, '⠂')]
    [InlineData(0, 2, '⠄')]
    [InlineData(1, 0, '⠈')]
    [InlineData(1, 1, '⠐')]
    [InlineData(1, 2, '⠠')]
    [InlineData(0, 3, '⡀')]
    [InlineData(1, 3, '⢀')]
    public void Each_dot_lights_the_braille_dot_in_the_same_place(int x, int y, char expected)
    {
        var canvas = new BrailleCanvas(1, 1);

        canvas.Set(x, y);

        Assert.Equal(expected, canvas.CharAt(0, 0));
    }

    [Fact]
    public void All_eight_dots_make_the_full_cell()
    {
        var canvas = new BrailleCanvas(1, 1);
        for (var x = 0; x < 2; x++)
        {
            for (var y = 0; y < 4; y++)
            {
                canvas.Set(x, y);
            }
        }

        Assert.Equal('⣿', canvas.CharAt(0, 0));
    }

    [Fact]
    public void Dots_off_the_canvas_are_ignored_rather_than_thrown()
    {
        var canvas = new BrailleCanvas(2, 1);

        canvas.Set(-1, 0);
        canvas.Set(4, 0);
        canvas.Set(0, 4);
        canvas.Line(-100, -100, -50, -50);

        Assert.All(canvas.Render(), row => Assert.Equal("⠀⠀", row));
    }

    [Fact]
    public void A_horizontal_line_crosses_every_cell_on_its_row()
    {
        var canvas = new BrailleCanvas(3, 1);

        canvas.Line(0, 0, 5, 0);

        Assert.Equal("⠉⠉⠉", canvas.Render()[0]);
    }
}

public class RadarProjectionTests
{
    [Fact]
    public void A_degree_of_longitude_is_drawn_shorter_than_a_degree_of_latitude_by_the_cosine()
    {
        var box = new GeoBox(55.0, 57.0, 10.0, 12.0);
        var p = new RadarProjection(box, 1000, 1000);

        var (xWest, _) = p.Project(56.0, 10.0);
        var (xEast, _) = p.Project(56.0, 11.0);
        var (_, yNorth) = p.Project(57.0, 11.0);
        var (_, ySouth) = p.Project(56.0, 11.0);

        Assert.Equal(Math.Cos(56.0 * Math.PI / 180.0), (xEast - xWest) / (ySouth - yNorth), 6);
    }

    [Fact]
    public void The_box_is_centred_and_fits_inside_the_canvas()
    {
        var p = new RadarProjection(RadarRegions.Kattegat, 200, 100);
        var box = RadarRegions.Kattegat;

        var (x0, y0) = p.Project(box.MaxLatDeg, box.MinLonDeg);
        var (x1, y1) = p.Project(box.MinLatDeg, box.MaxLonDeg);

        Assert.True(x0 >= 0 && y0 >= 0 && x1 <= 200 && y1 <= 100);
        Assert.Equal(200 - x1, x0, 6);
        Assert.Equal(100 - y1, y0, 6);
    }

    [Fact]
    public void One_degree_of_latitude_spans_sixty_nautical_miles()
    {
        var p = new RadarProjection(new GeoBox(55.0, 57.0, 10.0, 12.0), 400, 400);

        var (_, yNorth) = p.Project(57.0, 11.0);
        var (_, ySouth) = p.Project(56.0, 11.0);

        Assert.Equal(60.0, p.DotsToNm(ySouth - yNorth), 6);
    }
}

public class RadarFrameBuilderTests
{
    private static VesselOnScope Vessel(long mmsi, double lat, double lon, ScopeState state,
        double? cog = null, string name = "V") =>
        new(mmsi, name, lat, lon, cog, state, [(lat, lon)], DateTime.UnixEpoch);

    private static RadarScene Scene(IReadOnlyList<VesselOnScope> vessels, double sweepDeg = 0,
        IReadOnlyList<long>? labelled = null) =>
        new(new GeoBox(55.0, 57.0, 10.0, 13.6), 24, 8, [], vessels, sweepDeg, labelled ?? []);

    [Theory]
    [InlineData(0.0, '↑')]
    [InlineData(44.0, '↗')]
    [InlineData(90.0, '→')]
    [InlineData(181.0, '↓')]
    [InlineData(-90.0, '←')]
    [InlineData(338.0, '↑')]
    public void A_moving_vessel_points_along_its_reported_course(double cog, char arrow) =>
        Assert.Equal(arrow, RadarFrameBuilder.Glyph(Vessel(1, 56, 11, ScopeState.Moving, cog)));

    [Fact]
    public void A_vessel_with_no_course_gets_no_arrow()
    {
        // An arrow would point in a direction nobody reported.
        Assert.Equal('+', RadarFrameBuilder.Glyph(Vessel(1, 56, 11, ScopeState.Moving)));
    }

    [Theory]
    [InlineData(ScopeState.StoppedClaimingUnderWay)]
    [InlineData(ScopeState.MovingClaimingStationary)]
    public void Both_directions_of_the_forgotten_dial_draw_the_same_way(ScopeState state)
    {
        var frame = RadarFrameBuilder.Build(Scene([Vessel(1, 56.0, 11.8, state, 90)]));

        var cell = frame.Cells.Cast<Cell>().Single(c => c.Ink == Ink.Conflict);
        Assert.Equal('!', cell.Glyph);
    }

    [Fact]
    public void Golden_frame()
    {
        // Pins the whole composite: rings, the beam pointing due east, three vessels, and a name.
        // The ring label is absent on purpose: ALFA's name already holds that row, and a label
        // never overwrites another. If this changes, look at the new frame before accepting it.
        var frame = RadarFrameBuilder.Build(Scene(
            [
                Vessel(1, 56.5, 11.0, ScopeState.Moving, cog: 90, name: "ALFA"),
                Vessel(2, 55.5, 12.5, ScopeState.Stopped),
                Vessel(3, 56.0, 12.8, ScopeState.StoppedClaimingUnderWay),
            ],
            sweepDeg: 90,
            labelled: [1]));

        Assert.Equal(
            [
                "       ⡤⠒⠊⠉⠉⠉⠉⠉⠢⢄⡀      ",
                "     ⡰⠉ ⢀⠤⠔⠒⠒⠢⠤⡀ ⠘⢢     ",
                "    ⡜  ⡔→ ALFA ⠈⢢  ⢇    ",
                "    ⡇ ⢰⠁ ⡰⠁  ⠈⢆ ⠈⡆ ⢸    ",
                "    ⡇ ⠸⡀ ⠱⡀ ⠉⠉⠉⠉!⠉⠉⠉⠉⠉⠉⠉",
                "    ⢣  ⠣⡀ ⠉⠒⠒⠉ ⢀⠜  ⡎    ",
                "     ⠱⣀ ⠈⠒⠢⠤⠤⠔⠒o ⢠⠜     ",
                "       ⠓⠤⢄⣀⣀⣀⣀⣀⠔⠊⠁      ",
            ],
            frame.Text());
        Assert.Equal(20.0, frame.RingSpacingNm);
    }

    [Fact]
    public void A_name_never_overwrites_a_vessel()
    {
        var frame = RadarFrameBuilder.Build(Scene(
            [
                Vessel(1, 56.0, 11.0, ScopeState.Moving, cog: 90, name: "LONGNAME"),
                Vessel(2, 56.0, 11.8, ScopeState.Stopped),
            ],
            labelled: [1]));

        Assert.Contains(frame.Cells.Cast<Cell>(), c => c.Ink == Ink.Stopped);
        Assert.DoesNotContain(frame.Cells.Cast<Cell>(), c => c.Glyph == 'L');
    }

    [Theory]
    [InlineData(70.0, 20.0)]
    [InlineData(10.0, 2.0)]
    [InlineData(0.5, 1.0)]
    public void Range_rings_are_spaced_at_round_numbers(double maxRadiusNm, double expectedNm) =>
        Assert.Equal(expectedNm, RadarFrameBuilder.RingSpacing(maxRadiusNm));

    [Fact]
    public void Cells_just_behind_the_beam_are_lit_and_cells_ahead_of_it_are_not()
    {
        // Beam due north. East of centre is 90° ahead of it; west of centre is 90° behind it, in
        // the afterglow.
        var frame = RadarFrameBuilder.Build(Scene([], sweepDeg: 0));

        Assert.True(frame.Cells[4, 2].Lit);
        Assert.False(frame.Cells[4, 21].Lit);
    }
}

public class CitationTests
{
    [Fact]
    public void A_citation_prints_as_run_and_line() =>
        Assert.Equal("[r3·L48211]", new Citation(3, 48211).ToString());

    [Fact]
    public void Citations_are_found_in_text_with_either_separator()
    {
        var found = Citation.FindAll("stopped [r3·L48211] and left [r12.L9]; not [r3-L1] or [L5]");

        Assert.Equal([new Citation(3, 48211), new Citation(12, 9)], found);
    }
}
