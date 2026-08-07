using System.Text.Json.Serialization;

namespace SignedChannel;

/// <summary>
/// Base of every browser request. The page context travels on the envelope rather than being
/// asked for per action, so visit tracking and activity auditing work uniformly and an action
/// never has to remember to collect it.
/// </summary>
public record MessageWebBrowserRequestBase
{
    /// <summary>The page the request was made from.</summary>
    [JsonPropertyName("currentUrl")]
    public required string CurrentUrl { get; set; }

    /// <summary>The referring page, when the browser supplied one.</summary>
    [JsonPropertyName("referrerUrl")]
    public string? ReferrerUrl { get; set; }
}

/// <summary>
/// Base of every browser response.
/// </summary>
public record MessageWebBrowserResponseBase
{
    /// <summary>Identifies this invocation; pairs with long-running progress reporting.</summary>
    [JsonPropertyName("jobId")]
    public string? JobId { get; set; }

    /// <summary>Completion progress; 100 means complete, which is the case for a synchronous action.</summary>
    [JsonPropertyName("percentageComplete")]
    public byte PercentageComplete { get; set; } = 100;

    /// <summary>
    /// Server-authoritative instant this session expires. Stamped by the dispatcher onto every
    /// response of a signed-in session, so the client's idle countdown tracks real API activity
    /// without a separate round-trip. Null when not signed in.
    /// </summary>
    [JsonPropertyName("sessionExpiresAtUtc")]
    public DateTimeOffset? SessionExpiresAtUtc { get; set; }
}

/// <summary>
/// Outcome of an action's request validation. <see cref="Errors"/> is optional — the dispatcher
/// treats a null collection as "invalid, no detail" rather than failing.
/// </summary>
public sealed class WebBrowserActionsValidationResult
{
    /// <summary>Whether the request passed validation.</summary>
    public bool IsValid { get; set; }

    /// <summary>Field-keyed messages, in the shape of a problem-details validation payload.</summary>
    public Dictionary<string, string[]>? Errors { get; set; }
}
