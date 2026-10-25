using Microsoft.AspNetCore.SignalR;
using PublicKeyUtils.CryptoKeys;

namespace SignedChannel;

/// <summary>
/// Server-to-client push envelope. The payload is encrypted to the session's own key, so what
/// travels over the socket is meaningful only to the browser that registered it.
/// </summary>
public sealed class PushMessage
{
    /// <summary>What the client should do with this message — always one of the channel's own names.</summary>
    public required string ActionName { get; init; }

    /// <summary>The payload, encrypted to the session's encryption public key. Empty when there is none.</summary>
    public required string EncryptedPayloadAsBase64 { get; init; }

    /// <summary>Round-trip formatted instant after which the client should ignore this message.</summary>
    public required string ExpiryTimestampInUtc { get; init; }
}

/// <summary>
/// Pushes to a session's browser tabs over the presence hub.
/// <para>
/// The application pushes <em>instructions</em> (<see cref="PushInstructionAsync"/>): a name it
/// defines and a payload of any size. The instruction is stored, only a short pointer is pushed —
/// encrypted, because RSA payloads are size-limited — and the browser downloads the instruction
/// over the signed channel. The raw push primitives underneath are the channel's own, for its
/// lifecycle messages, and are not exposed.
/// </para>
/// <para>
/// One client-side method receives everything (<c>ReceiveEncryptedMessage</c>), so the hub surface
/// stays fixed no matter how many kinds of instruction the application grows. SignalR groups are
/// keyed by session id, which is what makes "tell every tab of this session" a single call.
/// </para>
/// </summary>
public class ChannelPushService
{
    /// <summary>The single client-side method every push arrives on.</summary>
    public const string ReceiveEncryptedMessageMethod = "ReceiveEncryptedMessage";

    private readonly IHubContext<PresenceHub> _hub;
    private readonly IChannelSessionStore _sessions;
    private readonly IChannelInstructionStore _instructions;

    /// <summary>Creates the push service.</summary>
    public ChannelPushService(IHubContext<PresenceHub> hub, IChannelSessionStore sessions, IChannelInstructionStore instructions)
    {
        _hub = hub;
        _sessions = sessions;
        _instructions = instructions;
    }

    /// <summary>
    /// Sends an instruction to every verified tab of the session, or to all but one.
    /// </summary>
    /// <param name="sessionId">The session to address.</param>
    /// <param name="name">The instruction's name — the application's own event name.</param>
    /// <param name="payload">Data for it, typically JSON; null when there is none.</param>
    /// <param name="validity">How long the browser has to collect it.</param>
    /// <param name="exceptConnectionId">
    /// A connection to leave out — typically the tab that caused the change, which already knows.
    /// </param>
    public async Task PushInstructionAsync(string sessionId, string name, string? payload, TimeSpan validity, string? exceptConnectionId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var pointer = await _instructions.StoreAsync(sessionId, new ChannelInstruction(name, payload), validity);
        var expiry = DateTimeOffset.UtcNow.Add(validity);

        if (exceptConnectionId is null)
        {
            await SendToSessionAsync(sessionId, ChannelNames.InstructionPush, pointer, expiry);
        }
        else
        {
            await SendToSessionExceptAsync(sessionId, exceptConnectionId, ChannelNames.InstructionPush, pointer, expiry);
        }
    }

    /// <summary>
    /// Pushes to one connection. Needed before a connection has been added to its session's group
    /// — during the connection challenge, for instance.
    /// </summary>
    internal async Task SendToConnectionAsync(string connectionId, string sessionId, string actionName, string? payload, DateTimeOffset expiry)
    {
        var message = await BuildAsync(sessionId, actionName, payload, expiry);
        await _hub.Clients.Client(connectionId).SendAsync(ReceiveEncryptedMessageMethod, message);
    }

    /// <summary>Pushes to every group-bound connection of the session.</summary>
    internal async Task SendToSessionAsync(string sessionId, string actionName, string? payload, DateTimeOffset expiry)
    {
        var message = await BuildAsync(sessionId, actionName, payload, expiry);
        await _hub.Clients.Group(sessionId).SendAsync(ReceiveEncryptedMessageMethod, message);
    }

    /// <summary>Pushes to the session's connections except one.</summary>
    internal async Task SendToSessionExceptAsync(string sessionId, string connectionIdToExclude, string actionName, string? payload, DateTimeOffset expiry)
    {
        var message = await BuildAsync(sessionId, actionName, payload, expiry);
        await _hub.Clients.GroupExcept(sessionId, connectionIdToExclude).SendAsync(ReceiveEncryptedMessageMethod, message);
    }

    /// <summary>Binds a connection to its session's group, so session-wide pushes reach it.</summary>
    internal Task AddConnectionToGroupAsync(string connectionId, string sessionId) =>
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
