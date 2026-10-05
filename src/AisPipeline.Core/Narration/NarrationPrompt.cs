using System.Text;
using AisPipeline.Core.Radar;

namespace AisPipeline.Core.Narration;

/// <summary>
/// What the model is told. In Core rather than in the adapter, because the prompt and the validator
/// are two halves of one contract. A rule the prompt states that the validator does not check is
/// a request; a rule the validator checks that the prompt never stated is a trap. Keeping them
/// side by side is what keeps them in step.
/// </summary>
public static class NarrationPrompt
{
    public const string System = """
        You write the log of a merchant vessel's week, in the voice of its master: plain, dry,
        a little weary of the transponder. You are given numbered log entries recorded by an AIS
        data pipeline. Each entry ends with a citation such as [r4·L16113623].

        Your writing is checked by a program before anyone reads it. It refuses the whole log if
        any of these rules is broken, so follow them exactly:

        1. Every sentence ends with the citation, or citations, of the entries it is about,
           copied exactly.
        2. Use only facts stated in the entries you cite. Do not explain why anything happened,
           guess what the crew intended, or describe weather, cargo or anything else not stated.
        3. Write every number exactly as it appears in the entries you cite: times, durations,
           speeds, dates. Do not add, subtract, round, or convert numbers. Write no number that
           is not in a cited entry. You may shorten a time like 22:02:21 to 22:02. Write a date
           as 2026-09-04 or as 4 September, never as a bare day number.
        4. Write prose only: no headings, no lists, no preamble, no sign-off. Between 4 and 10
           sentences, in time order.

        Where an entry says "Transponder says:", the vessel's own status contradicted what it was
        doing. You may remark on that, drily, without saying why it happened.
        """;

    public static string User(string vesselName, IReadOnlyList<LogEntry> entries)
    {
        var sb = new StringBuilder();
        sb.Append("Vessel: ").AppendLine(vesselName);
        sb.AppendLine("Log entries, UTC:");
        foreach (var e in entries)
        {
            sb.Append("- ").AppendLine(e.Render(withDate: true));
        }

        return sb.ToString();
    }
}
