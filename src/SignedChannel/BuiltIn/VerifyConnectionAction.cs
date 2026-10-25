using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace SignedChannel.BuiltIn;

/// <summary>Request of <see cref="ChannelNames.VerifyConnection"/>.</summary>
public sealed record VerifyConnectionRequest : MessageWebBrowserRequestBase
{
    /// <summary>The decrypted challenge token.</summary>
    [JsonPropertyName("token")]
    public required string Token { get; init; }
}

/// <summary>Answer to <see cref="ChannelNames.VerifyConnection"/>.</summary>
public sealed record VerifyConnectionResponse : MessageWebBrowserResponseBase
{
    /// <summary>Whether the connection is now bound to its session.</summary>
    [JsonPropertyName("verified")]
    public bool Verified { get; init; }
}

/// <summary>
/// The channel-binding step. The browser returns the challenge token, decrypted, inside a signed
/// request; only a holder of the session's private key could have read it. If it checks out, the
/// connection joins the session's group — and only then can it receive session-wide pushes.
/// </summary>
internal sealed class VerifyConnectionAction : ISecureWebBrowserAction<VerifyConnectionRequest, VerifyConnectionResponse>
{
    private static readonly TimeSpan VerifiedPushValidity = TimeSpan.FromSeconds(60);

    private readonly IChannelTokenStore _tokens;
    private readonly ChannelPushService _push;
    private readonly IServiceProvider _serviceProvider;

    public VerifyConnectionAction(IChannelTokenStore tokens, ChannelPushService push, IServiceProvider serviceProvider)
    {
        _tokens = tokens;
        _push = push;
        _serviceProvider = serviceProvider;
    }

    public WebBrowserActionsValidationResult ValidateFromWebBrowser(string sessionId, string? userId, string? latestDeviceId, VerifyConnectionRequest? request) =>
        new() { IsValid = !string.IsNullOrWhiteSpace(request?.Token) };

    public Task<bool> HasAccess(HttpRequest unusedHttpRequest, string sessionId, string? userId, string? latestDeviceId, VerifyConnectionRequest? unusedRequest) =>
        Task.FromResult(true);

    public async Task<VerifyConnectionResponse> ProcessMessageFromWebBrowserAsync(
        HttpRequest unusedHttpRequest, string sessionId, string signalRConnectionId, string? userId, string? latestDeviceId,
        VerifyConnectionRequest? request, string jobId, CancellationToken cancellationToken)
    {
        var verified = await _tokens.ConsumeAndValidateAsync(
            ChannelNames.ConnectionChallengeTokenId(sessionId, signalRConnectionId), request!.Token);
        if (!verified)
        {
            return new VerifyConnectionResponse { Verified = false };
        }

        await _push.AddConnectionToGroupAsync(signalRConnectionId, sessionId);

        // Sent through the group rather than to the connection, so it proves the binding as well as
        // announcing it.
        await _push.SendToSessionAsync(
            sessionId, ChannelNames.ConnectionVerifiedPush, payload: null, DateTimeOffset.UtcNow.Add(VerifiedPushValidity));

        if (!string.IsNullOrEmpty(userId))
        {
            var notifier = _serviceProvider.GetService<IConnectionChangeNotifier>();
            if (notifier is not null)
            {
                // Detached, as on disconnect: a slow notification channel must not hold this up.
                _ = notifier.OnSessionConnectionChangedAsync(userId, sessionId);
            }
        }

        return new VerifyConnectionResponse { Verified = true };
    }
}
