using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;

namespace SignedChannel.BuiltIn;

/// <summary>Answer to <see cref="ChannelNames.KeepAlive"/>. The expiry itself rides
/// <see cref="MessageWebBrowserResponseBase.SessionExpiresAtUtc"/>, stamped by the dispatcher.</summary>
public sealed record KeepAliveResponse : MessageWebBrowserResponseBase
{
    /// <summary>Seconds before expiry the client should warn.</summary>
    [JsonPropertyName("warningSeconds")]
    public int WarningSeconds { get; init; }

    /// <summary>Server clock at response time.</summary>
    [JsonPropertyName("serverTimeUtc")]
    public DateTimeOffset ServerTimeUtc { get; init; }
}

/// <summary>
/// "Keep me signed in" — the warning's Stay button, and the client's re-check at zero. The
/// dispatcher has already slid the idle window just by accepting this signed request and stamps
/// the fresh expiry on the response, so there is nothing left to do here. Past the absolute cap
/// the expiry comes back in the past and the client signs out: the server decides, not the client.
/// </summary>
internal sealed class KeepAliveAction : ISecureWebBrowserAction<MessageWebBrowserRequestBase, KeepAliveResponse>
{
    private readonly SessionExpiryPolicy _sessionExpiry;

    public KeepAliveAction(SessionExpiryPolicy sessionExpiry) => _sessionExpiry = sessionExpiry;

    public WebBrowserActionsValidationResult ValidateFromWebBrowser(string sessionId, string? userId, string? latestDeviceId, MessageWebBrowserRequestBase? unusedRequest) =>
        new() { IsValid = !string.IsNullOrEmpty(sessionId) };

    public Task<bool> HasAccess(HttpRequest unusedHttpRequest, string sessionId, string? userId, string? latestDeviceId, MessageWebBrowserRequestBase? unusedRequest) =>
        Task.FromResult(true);

    public Task<KeepAliveResponse> ProcessMessageFromWebBrowserAsync(
        HttpRequest unusedHttpRequest, string sessionId, string signalRConnectionId, string? userId, string? latestDeviceId,
        MessageWebBrowserRequestBase? unusedRequest, string jobId, CancellationToken cancellationToken) =>
        Task.FromResult(new KeepAliveResponse
        {
            WarningSeconds = _sessionExpiry.WarningSeconds,
            ServerTimeUtc = DateTimeOffset.UtcNow
        });
}
