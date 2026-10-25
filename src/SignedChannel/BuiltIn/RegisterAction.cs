using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SignedChannel.BuiltIn;

/// <summary>Answer to <see cref="ChannelNames.Register"/>.</summary>
public sealed record RegisterResponse : MessageWebBrowserResponseBase
{
    /// <summary>Whether the session is signed in right now.</summary>
    [JsonPropertyName("isSignedIn")]
    public bool IsSignedIn { get; init; }

    /// <summary>Seconds before expiry the client should warn; 0 when not signed in.</summary>
    [JsonPropertyName("warningSeconds")]
    public int WarningSeconds { get; init; }

    /// <summary>Server clock at response time, so the client can correct for skew when counting down.</summary>
    [JsonPropertyName("serverTimeUtc")]
    public DateTimeOffset ServerTimeUtc { get; init; }
}

/// <summary>
/// First request after a connection opens: records the connection on the session, then pushes it
/// the connection challenge — a single-use token encrypted to the session key. The connection is
/// <em>not</em> in the session's group yet; that happens only once <see cref="VerifyConnectionAction"/>
/// sees the token come back.
/// </summary>
internal sealed class RegisterAction : ISecureWebBrowserAction<MessageWebBrowserRequestBase, RegisterResponse>
{
    private static readonly TimeSpan ChallengeValidity = TimeSpan.FromMinutes(10);

    private readonly IChannelSessionStore _sessions;
    private readonly IChannelTokenStore _tokens;
    private readonly ChannelPushService _push;
    private readonly ConnectionRegistry _registry;
    private readonly SessionExpiryPolicy _sessionExpiry;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<RegisterAction> _logger;

    public RegisterAction(
        IChannelSessionStore sessions, IChannelTokenStore tokens, ChannelPushService push, ConnectionRegistry registry,
        SessionExpiryPolicy sessionExpiry, IServiceProvider serviceProvider, ILogger<RegisterAction> logger)
    {
        _sessions = sessions;
        _tokens = tokens;
        _push = push;
        _registry = registry;
        _sessionExpiry = sessionExpiry;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public WebBrowserActionsValidationResult ValidateFromWebBrowser(string sessionId, string? userId, string? latestDeviceId, MessageWebBrowserRequestBase? unusedRequest) =>
        new() { IsValid = !string.IsNullOrEmpty(sessionId) };

    public Task<bool> HasAccess(HttpRequest unusedHttpRequest, string sessionId, string? userId, string? latestDeviceId, MessageWebBrowserRequestBase? unusedRequest) =>
        Task.FromResult(true);

    public async Task<RegisterResponse> ProcessMessageFromWebBrowserAsync(
        HttpRequest unusedHttpRequest, string sessionId, string signalRConnectionId, string? userId, string? latestDeviceId,
        MessageWebBrowserRequestBase? unusedRequest, string jobId, CancellationToken cancellationToken)
    {
        var session = await _sessions.GetByIdAsync(sessionId);
        if (session is null)
        {
            return new RegisterResponse { ServerTimeUtc = DateTimeOffset.UtcNow };
        }

        await _sessions.AddConnectionIdAsync(sessionId, signalRConnectionId);

        var observer = _serviceProvider.GetService<IPresenceObserver>();
        if (observer is not null)
        {
            await observer.OnConnectionBoundAsync(signalRConnectionId, sessionId, session.WebBrowserId);
        }

        _logger.LogInformation("Presence bind {ConnectionId} -> session {SessionId}", signalRConnectionId, sessionId);

        // The register-vs-disconnect race: if the socket died while this request was in flight, its
        // disconnect already ran and found nothing to remove. Undo what was just added, or the dead
        // connection counts forever.
        if (!_registry.IsLive(signalRConnectionId))
        {
            await _sessions.RemoveConnectionIdAsync(signalRConnectionId);
            if (observer is not null)
            {
                await observer.OnDisconnectedAsync(signalRConnectionId);
            }
            return new RegisterResponse { ServerTimeUtc = DateTimeOffset.UtcNow };
        }

        var token = await _tokens.GenerateAsync(
            ChannelNames.ConnectionChallengeTokenId(sessionId, signalRConnectionId), ChallengeValidity);

        // A little short of the token's own lifetime, so the browser never answers with one that
        // has already gone.
        await _push.SendToConnectionAsync(
            signalRConnectionId, sessionId, ChannelNames.ConnectionChallengePush,
            payload: token, expiry: DateTimeOffset.UtcNow.Add(ChallengeValidity).AddSeconds(-20));

        // userId arrives already logout-aware from the dispatcher, so a tab refreshed after a remote
        // sign-out lands signed out rather than resuming.
        var isSignedIn = !string.IsNullOrEmpty(userId);
        return new RegisterResponse
        {
            IsSignedIn = isSignedIn,
            WarningSeconds = isSignedIn ? _sessionExpiry.WarningSeconds : 0,
            ServerTimeUtc = DateTimeOffset.UtcNow
        };
    }
}
