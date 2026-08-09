using Microsoft.AspNetCore.SignalR;
using PublicKeyUtils.CryptoKeys;

namespace SignedChannel;

/// <summary>
/// Server-to-client push envelope. The payload is encrypted to the session's own key, so what
/// travels over the socket is meaningful only to the browser that registered it.
/// </summary>
public sealed class PushMessage
{
    /// <summary>What the client should do with this message.</summary>
    public required string ActionName { get; init; }

    /// <summary>The payload, encrypted to the session's encryption public key. Empty when there is none.</summary>
    public required string EncryptedPayloadAsBase64 { get; init; }

    /// <summary>Round-trip formatted instant after which the client should ignore this message.</summary>
    public required string ExpiryTimestampInUtc { get; init; }
}

/// <summary>
/// Pushes encrypted messages to a session's connections over the presence hub.
/// <para>
/// One client-side method receives everything (<c>ReceiveEncryptedMessage</c>) with the real
/// intent inside the encrypted payload, so the hub surface stays fixed no matter how many kinds of
/// message the application grows. SignalR groups are keyed by session id, which is what makes
/// "tell every tab of this session" a single call.
/// </para>
/// <para>
/// Payloads are encrypted with RSA and are therefore size-limited. For anything larger than a
/// short string, push a pointer and let the client fetch the body over the signed channel.
/// </para>
/// </summary>
public class ChannelPushService
{
    /// <summary>The single client-side method every push arrives on.</summary>
    public const string ReceiveEncryptedMessageMethod = "ReceiveEncryptedMessage";

    private readonly IHubContext<PresenceHub> _hub;
    private readonly IChannelSessionStore _sessions;

    /// <summary>Creates the push service.</summary>
    public ChannelPushService(IHubContext<PresenceHub> hub, IChannelSessionStore sessions)
    {
        _hub = hub;
        _sessions = sessions;
    }

    /// <summary>
    /// Pushes to one connection. Needed before a connection has been added to its session's group
    /// — during the connection challenge, for instance.
    /// </summary>
    public async Task SendToConnectionAsync(string connectionId, string sessionId, string actionName, string? payload, DateTimeOffset expiry)
    {
        var message = await BuildAsync(sessionId, actionName, payload, expiry);
        await _hub.Clients.Client(connectionId).SendAsync(ReceiveEncryptedMessageMethod, message);
    }

    /// <summary>Pushes to every group-bound connection of the session.</summary>
    public async Task SendToSessionAsync(string sessionId, string actionName, string? payload, DateTimeOffset expiry)
    {
        var message = await BuildAsync(sessionId, actionName, payload, expiry);
        await _hub.Clients.Group(sessionId).SendAsync(ReceiveEncryptedMessageMethod, message);
    }

    /// <summary>
    /// Pushes to the session's connections except one — typically the tab that caused the change,
    /// which already knows.
    /// </summary>
    public async Task SendToSessionExceptAsync(string sessionId, string connectionIdToExclude, string actionName, string? payload, DateTimeOffset expiry)
    {
        var message = await BuildAsync(sessionId, actionName, payload, expiry);
        await _hub.Clients.GroupExcept(sessionId, connectionIdToExclude).SendAsync(ReceiveEncryptedMessageMethod, message);
    }

    /// <summary>Binds a connection to its session's group, so session-wide pushes reach it.</summary>
    public Task AddConnectionToGroupAsync(string connectionId, string sessionId) =>
        _hub.Groups.AddToGroupAsync(connectionId, sessionId);

    private async Task<PushMessage> BuildAsync(string sessionId, string actionName, string? payload, DateTimeOffset expiry)
    {
        var encrypted = string.Empty;
        if (!string.IsNullOrWhiteSpace(payload))
        {
            var session = await _sessions.GetByIdAsync(sessionId)
                ?? throw new InvalidOperationException($"Session not found: {sessionId}");
            var encryptionKey = ChannelCrypto.Deserialize<EncryptDecryptPublicKey>(session.EncryptionPublicKeyBase64);
            encrypted = Convert.ToBase64String(encryptionKey.Encrypt(payload));
        }

        return new PushMessage
        {
            ActionName = actionName,
            EncryptedPayloadAsBase64 = encrypted,
            ExpiryTimestampInUtc = expiry.ToString("o")
        };
    }
}
