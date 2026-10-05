using AisPipeline.Core.Domain;
using AisPipeline.Core.Narration;
using AisPipeline.Core.Radar;

namespace AisPipeline.Tests.Narration;

/// <summary>
/// The validator is the whole safety case for letting a language model write about the data, so
/// each test is a way a fluent paragraph could be wrong while reading perfectly well.
/// </summary>
public class CitedNarrativeTests
{
    private static readonly LogEntry Anchored = new(
        new DateTime(2026, 9, 3, 15, 38, 43, DateTimeKind.Utc), 636025106, LogKind.Stopped,
        "STINGRAY stopped on its Fredericia call.", new Citation(3, 11733590));

    private static readonly LogEntry Aweigh = new(
        new DateTime(2026, 9, 4, 22, 2, 21, DateTimeKind.Utc), 636025106, LogKind.StopEnded,
        "STINGRAY ends a 30.4 h stop on its Fredericia call.", new Citation(4, 16113623));

    private static readonly LogEntry Berthed = new(
        new DateTime(2026, 9, 4, 22, 47, 10, DateTimeKind.Utc), 636025106, LogKind.StoppedClaimingUnderWay,
        "STINGRAY stopped on its Fredericia call. Transponder says: UNDER WAY USING ENGINE.",
        new Citation(4, 16609354));

    private static readonly IReadOnlyList<LogEntry> Log = [Anchored, Aweigh, Berthed];

    [Fact]
    public void A_fully_cited_passage_that_quotes_its_numbers_is_accepted()
    {
        var verdict = CitedNarrative.Validate(
            "Brought up off Fredericia at 15:38 on 3 September [r3·L11733590]. " +
            "Weighed anchor at 22:02 after 30.4 h of waiting [r4·L16113623]. " +
            "Alongside by 22:47, and the transponder still swears we are under way using engine [r4·L16609354].",
            Log);

        Assert.True(verdict.Accepted, string.Join("; ", verdict.Problems));
        Assert.Equal(3, verdict.Sentences.Count);
    }

    [Fact]
    public void A_sentence_with_no_citation_refuses_the_whole_narration()
    {
        var verdict = CitedNarrative.Validate(
            "Weighed anchor at 22:02 [r4·L16113623]. The crew were glad of it.", Log);

        Assert.False(verdict.Accepted);
        Assert.Contains(verdict.Problems, p => p.StartsWith("sentence 2 cites nothing", StringComparison.Ordinal));
    }

    [Fact]
    public void An_invented_time_is_refused()
    {
        var verdict = CitedNarrative.Validate("Weighed anchor at 22:15 [r4·L16113623].", Log);

        Assert.Equal(["sentence 1 says 22:15, which none of its citations contain"], verdict.Problems);
    }

    [Fact]
    public void A_rounded_figure_is_refused_because_rounding_moves_it()
    {
        var verdict = CitedNarrative.Validate("A 30 h wait at anchor [r4·L16113623].", Log);

        Assert.False(verdict.Accepted);
        Assert.Contains(verdict.Problems, p => p.Contains("says 30,", StringComparison.Ordinal));
    }

    [Fact]
    public void Arithmetic_across_entries_is_refused_even_when_both_inputs_are_cited()
    {
        // 15:38 to 22:47 the next day is real arithmetic on real entries, and the result is still a
        // number nobody stored. The pipeline computes durations in detection, where it is tested.
        var verdict = CitedNarrative.Validate(
            "From 15:38 to 22:47 was 31 hours [r3·L11733590] [r4·L16609354].", Log);

        Assert.Equal(["sentence 1 says 31, which none of its citations contain"], verdict.Problems);
    }

    [Fact]
    public void A_number_true_of_another_entry_is_refused_when_that_entry_is_not_cited()
    {
        var verdict = CitedNarrative.Validate("Weighed anchor at 22:47 [r4·L16113623].", Log);

        Assert.False(verdict.Accepted);
    }

    [Fact]
    public void A_citation_to_a_line_the_model_was_not_given_is_refused()
    {
        var verdict = CitedNarrative.Validate("Sailed at dawn [r4·L99].", Log);

        Assert.Equal(["sentence 1 cites [r4·L99], which is not in the log it was given"], verdict.Problems);
    }

    [Fact]
    public void The_numbers_inside_a_citation_are_not_facts_the_sentence_may_state()
    {
        // Run 7, line 120, on an entry whose date and time contain neither number.
        var entry = new LogEntry(new DateTime(2026, 9, 5, 10, 0, 0, DateTimeKind.Utc), 1, LogKind.Stopped,
            "X stopped.", new Citation(7, 120));

        var verdict = CitedNarrative.Validate("Waited 7 hours, then 120 more [r7·L120].", [entry]);

        Assert.Equal(
            ["sentence 1 says 7, which none of its citations contain", "sentence 1 says 120, which none of its citations contain"],
            verdict.Problems);
    }

    [Fact]
    public void Truncating_seconds_is_allowed_and_dates_compare_without_leading_zeros()
    {
        var verdict = CitedNarrative.Validate("On 4 September at 22:02 the anchor came up [r4·L16113623].", Log);

        Assert.True(verdict.Accepted, string.Join("; ", verdict.Problems));
    }

    [Theory]
    [InlineData("Waited 4 hours [r4·L16113623].", "4")]
    [InlineData("Made 9 knots [r4·L16113623].", "9")]
    [InlineData("Anchored for 2026 minutes [r4·L16113623].", "2026")]
    public void A_date_part_does_not_license_a_bare_number(string sentence, string invented)
    {
        // The cited entry is dated 2026-09-04. Its month, day and year are a date, not figures a
        // sentence may reuse as a duration or a speed: small integers are what a model invents.
        var verdict = CitedNarrative.Validate(sentence, Log);

        Assert.Equal([$"sentence 1 says {invented}, which none of its citations contain"], verdict.Problems);
    }

    [Theory]
    [InlineData("On 2026-09-04 at 22:02 the anchor came up [r4·L16113623].")]
    [InlineData("On September 4th, 2026, at 22:02 the anchor came up [r4·L16113623].")]
    [InlineData("On the 4th of September at 22:02 the anchor came up [r4·L16113623].")]
    public void A_date_written_as_a_date_matches_the_cited_entrys_date(string sentence)
    {
        var verdict = CitedNarrative.Validate(sentence, Log);

        Assert.True(verdict.Accepted, string.Join("; ", verdict.Problems));
    }

    [Fact]
    public void A_date_the_cited_entry_does_not_carry_is_refused()
    {
        var verdict = CitedNarrative.Validate("On 5 September the anchor came up [r4·L16113623].", Log);

        Assert.Equal(["sentence 1 says 5 September, which none of its citations is dated"], verdict.Problems);
    }

    [Fact]
    public void A_citation_after_the_full_stop_stays_with_its_sentence()
    {
        var sentences = CitedNarrative.Sentences("Anchored at 15:38. [r3·L11733590] Weighed at 22:02. [r4·L16113623]");

        Assert.Equal(["Anchored at 15:38. [r3·L11733590]", "Weighed at 22:02. [r4·L16113623]"], sentences);
    }

    [Fact]
    public void An_empty_reply_is_not_an_accepted_narration()
    {
        Assert.False(CitedNarrative.Validate("   ", Log).Accepted);
    }

    [Fact]
    public void The_prompt_carries_every_entry_and_its_citation()
    {
        var user = NarrationPrompt.User("STINGRAY", Log);

        Assert.All(Log, e => Assert.Contains(e.Render(withDate: true), user, StringComparison.Ordinal));
        Assert.Contains("Do not add, subtract, round", NarrationPrompt.System, StringComparison.Ordinal);
    }
}
