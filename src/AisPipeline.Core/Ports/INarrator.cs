namespace AisPipeline.Core.Ports;

/// <summary>
/// Something that writes prose from a prompt. The only port in the pipeline that leaves the
/// machine (ADR-0050).
///
/// Its output is never trusted: whatever comes back goes through
/// <see cref="Narration.CitedNarrative.Validate"/> before anyone reads it. That is why the port is
/// this thin. The adapter's only job is to carry text there and back, and every judgment about
/// that text is made in Core, where it is tested.
/// </summary>
public interface INarrator
{
    /// <summary>Who wrote it, for the line that says so.</summary>
    string Name { get; }

    Task<NarratorReply> NarrateAsync(string system, string user, CancellationToken cancellationToken);
}

/// <summary>The text, or the reason there is none. A declined request is an answer, not an error.</summary>
public sealed record NarratorReply(string? Text, string? Declined);
