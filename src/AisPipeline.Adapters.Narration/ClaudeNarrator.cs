using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta;
using Anthropic.Models.Beta.Messages;
using AisPipeline.Core.Ports;

namespace AisPipeline.Adapters.Narration;

/// <summary>
/// Narration by Claude, over the official Anthropic SDK.
///
/// The first and only adapter that sends anything off the machine (ADR-0050). What it sends is the
/// prompt Core built: a vessel's name and its ship's-log lines, which are derived from the public
/// DMA feed. No Statement of Facts, charter party term or anything else a user supplied is ever in
/// it.
///
/// Credentials resolve the way the SDK resolves them: <c>ANTHROPIC_API_KEY</c> first, then the
/// other sources it knows. The caller asks <see cref="Configured"/> before constructing one, so a
/// machine with no credentials gets the deterministic log and a sentence saying why, not an
/// exception.
/// </summary>
public sealed class ClaudeNarrator : INarrator
{
    /// <summary>The default model. Overridable with <c>--model</c> on the CLI.</summary>
    public const string DefaultModel = "claude-opus-5-5";

    /// <summary>
    /// Room for a ten-sentence log plus the model's thinking, which on Opus 5.5 cannot be turned
    /// off and is billed inside this ceiling.
    /// </summary>
    private const int MaxTokens = 16_000;

    private readonly AnthropicClient _client;
    private readonly string _model;

    public ClaudeNarrator(string model)
    {
        _client = new AnthropicClient();
        _model = model;
    }

    public string Name => _model;

    /// <summary>
    /// True when the SDK has an obvious credential to use. Deliberately conservative: an OAuth
    /// profile the SDK could also find is not checked here, and a user relying on one can set
    /// <c>ANTHROPIC_API_KEY</c> or pass <c>--narrate</c> anyway and read the error.
    /// </summary>
    public static bool Configured() =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"))
        || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN"));

    /// <summary>
    /// Failures come back as a reason, not an exception. The caller's answer to every one of them
    /// is the same (print why, then print the deterministic log) and a stack trace in place of a
    /// ship's log would be the opposite of telling the reader what happened. The SDK has already
    /// retried 429s, 5xx and dropped connections twice by the time any of these arrive.
    /// </summary>
    public async Task<NarratorReply> NarrateAsync(string system, string user, CancellationToken cancellationToken)
    {
        try
        {
            return await Ask(system, user, cancellationToken);
        }
        catch (AnthropicUnauthorizedException)
        {
            return new NarratorReply(null, "the API refused the credentials (401)");
        }
        catch (AnthropicNotFoundException)
        {
            return new NarratorReply(null, $"the API does not know the model '{_model}' (404)");
        }
        catch (AnthropicRateLimitException)
        {
            return new NarratorReply(null, "rate limited (429), still after the SDK's retries");
        }
        catch (Anthropic5xxException e)
        {
            return new NarratorReply(null, $"the API failed ({e.Message})");
        }
        catch (AnthropicApiException e)
        {
            return new NarratorReply(null, $"the API rejected the request ({e.Message})");
        }
        catch (AnthropicIOException e)
        {
            return new NarratorReply(null, $"could not reach the API ({e.Message})");
        }
    }

    private async Task<NarratorReply> Ask(string system, string user, CancellationToken cancellationToken)
    {
        var response = await _client.Beta.Messages.Create(new MessageCreateParams
        {
            Model = _model,
            MaxTokens = MaxTokens,
            System = system,

            // Low effort: rewording a dozen log lines is not a hard problem, and the validator,
            // not the model's diligence, is what makes the output safe to print.
            OutputConfig = new BetaOutputConfig { Effort = Effort.Low },

            // If a safety classifier declines, re-serve inside the same call on the fallback the
            // API picks for that refusal category, rather than returning nothing. "default"
            // rather than a pinned model, so a deprecated fallback is not a migration owed here.
            // The reply goes through the same validator whichever model wrote it.
            Betas = [AnthropicBeta.ServerSideFallback2026_07_01],
            Fallbacks = new Default(),

            Messages = [new() { Role = Role.User, Content = user }],
        }, cancellationToken);

        if (response.StopReason == "refusal")
        {
            return new NarratorReply(null,
                $"the model declined{(response.StopDetails is { } d ? $" ({d.Category})" : "")}");
        }

        var text = string.Join("\n",
            response.Content.Select(b => b.Value).OfType<BetaTextBlock>().Select(t => t.Text));

        return response.StopReason == "max_tokens"
            ? new NarratorReply(null, "the reply was cut off at the token limit")
            : new NarratorReply(text, null);
    }
}
