using AisPipeline.Core.Voyage;

namespace AisPipeline.Tests.Voyage;

/// <summary>
/// Shapes, never places.
///
/// The line this has to hold is that it classifies strings and never decides which port a string
/// means. SKAW and SKAGEN are both free text here, and nothing in these tests asserts they are the
/// same harbour -- because the data cannot show that, and claiming it would be the kind of quiet
/// invention the rest of the pipeline exists to refuse.
/// </summary>
public class DestinationClassifierTests
{
    private static DestinationShape Shape(string text) =>
        DestinationClassifier.Classify(new DestinationCount { Text = text }).Shape;

    [Theory]
    [InlineData("DKSKA")]
    [InlineData("SEGOT")]
    [InlineData("DK SKA")]
    [InlineData("SE GOT")]
    [InlineData("NLRTM")]
    public void Five_characters_beginning_with_a_country_are_locode_shaped(string text) =>
        Assert.Equal(DestinationShape.UnLocode, Shape(text));

    [Theory]
    [InlineData("SKAGEN")]
    [InlineData("SKAW")]
    [InlineData("GOTHENBURG")]
    [InlineData("PORT SAID")]
    [InlineData("GULF OF FINLAND")]
    public void A_place_written_the_way_a_person_says_it_is_free_text(string text) =>
        Assert.Equal(DestinationShape.FreeText, Shape(text));

    [Theory]
    [InlineData("FOR ORDERS")]
    [InlineData("BALTIC FOR ORDER")]
    [InlineData("SUEZ TO ORDER")]
    [InlineData("BCLTIC FOR ORDER")]
    public void Not_knowing_yet_is_its_own_shape(string text) =>
        Assert.Equal(DestinationShape.ForOrders, Shape(text));

    [Fact]
    public void Not_knowing_yet_outranks_the_route_arrow()
    {
        // "SKAW FOR ORDER" and "EG SUZ FOR ORDER" say the same thing -- no destination yet --
        // however much else is in the string, so that reading wins.
        Assert.Equal(DestinationShape.ForOrders, Shape("FISKV>BEANR FOR ORDERS"));
    }

    [Theory]
    [InlineData("FR DKK  > LT KLJ")]
    [InlineData("SEGOT>DKFDH")]
    [InlineData("FISKV>BEANR VIA NOK")]
    [InlineData("PLGDN=>NOMON")]
    [InlineData("SEGOT<SEKLR")]
    [InlineData("RU<PWE")]
    public void An_arrow_means_a_route_in_a_field_sized_for_one_place(string text) =>
        Assert.Equal(DestinationShape.Route, Shape(text));

    [Fact]
    public void An_arrow_counts_whichever_way_the_crew_pointed_it()
    {
        // Both appear in the feed. An earlier version accepted only ">" and filed PLGDN=>NOMON
        // and SEGOT<SEKLR under corruption, which said more about the classifier than the crew.
        Assert.Equal(Shape("SEGOT>SEKLR"), Shape("SEGOT<SEKLR"));
    }

    [Fact]
    public void Quoting_part_of_a_berth_name_is_not_corruption()
    {
        // LOMMA ANCH."B" is a person naming anchorage B, punctuation and all.
        Assert.Equal(DestinationShape.FreeText, Shape("LOMMA ANCH.\"B\""));
    }

    [Fact]
    public void Characters_no_keyboard_puts_there_on_purpose_are_unreadable()
    {
        // Real, from the seven-day window: a LOCODE followed by bracket and star debris.
        Assert.Equal(DestinationShape.Unreadable, Shape("GIGIB           *,(]"));

        // A control character is not something a crew typed.
        Assert.Equal(DestinationShape.Unreadable, Shape("SEGOT\u0001"));
    }

    [Fact]
    public void A_trailing_space_is_not_corruption()
    {
        // Somebody's thumb, not a broken transponder. It collapses under the normalising rule.
        Assert.Equal(DestinationShape.UnLocode, Shape("SEGOT "));
        Assert.Equal("SEGOT", DestinationClassifier.Normalise("SEGOT "));
    }

    [Fact]
    public void Spacing_and_case_stop_counting_but_nothing_else_does()
    {
        Assert.Equal("SEGOT", DestinationClassifier.Normalise("se got"));
        Assert.Equal("SEGOT", DestinationClassifier.Normalise("SE GOT"));

        // The weakest collapsing rule available, deliberately. These two are different words and
        // stay different: deciding they are one port is a claim about the world, not the string.
        Assert.NotEqual(
            DestinationClassifier.Normalise("SKAW"),
            DestinationClassifier.Normalise("SKAGEN"));
    }

    [Fact]
    public void A_pilot_exemption_number_typed_into_the_destination_is_not_a_locode()
    {
        // Real: crews put their PEC number in the only free-text field they have.
        Assert.Equal(DestinationShape.FreeText, Shape("SE GOT PEC-48"));
        Assert.Equal(DestinationShape.FreeText, Shape("SEGOT  PEC# 10-0505"));
    }

    [Fact]
    public void Distinct_counts_are_reported_before_and_after_the_collapsing_rule()
    {
        var built = TypedDestinationsBuilder.Build(
            [
                new() { Text = "SEGOT", Vessels = 26, Fixes = 133992 },
                new() { Text = "SE GOT", Vessels = 13, Fixes = 68272 },
                new() { Text = "SKAGEN", Vessels = 5, Fixes = 67775 },
            ],
            vesselsTyping: 44,
            vesselsThatChangedIt: 20);

        Assert.Equal(3, built.DistinctStrings);
        Assert.Equal(2, built.DistinctIgnoringSpacingAndCase);
        Assert.Equal(270039, built.FixesWithADestination);
    }

    [Fact]
    public void Destinations_are_ordered_by_how_much_of_the_feed_carried_them()
    {
        var built = TypedDestinationsBuilder.Build(
            [
                new() { Text = "SKAGEN", Fixes = 10 },
                new() { Text = "DKSKA", Fixes = 500 },
                new() { Text = "SKAW", Fixes = 100 },
            ],
            vesselsTyping: 3,
            vesselsThatChangedIt: 0);

        Assert.Equal(["DKSKA", "SKAW", "SKAGEN"], built.Destinations.Select(d => d.Text));
        Assert.Equal(500, built.FixesOfShape(DestinationShape.UnLocode));
        Assert.Equal(110, built.FixesOfShape(DestinationShape.FreeText));
    }
}
