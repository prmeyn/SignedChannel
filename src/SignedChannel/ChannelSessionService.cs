namespace SignedChannel;

/// <summary>
/// Signs sessions in and out, and tells their tabs.
/// <para>
/// The channel still never decides who someone is: the application authenticates however it likes
/// and then calls <see cref="SignInAsync"/> with its own opaque user id. Going through here rather
/// than the store directly is what keeps every tab of the session in step with the server.
/// </para>
/// </summary>
public class ChannelSessionService
{
    private static readonly TimeSpan LifecyclePushValidity = TimeSpan.FromSeconds(30);

    private readonly IChannelSessionStore _sessions;
    private readonly ChannelPushService _push;

    /// <summary>Creates the service.</summary>
    public ChannelSessionService(IChannelSessionStore sessions, ChannelPushService push)
    {
        _sessions = sessions;
        _push = push;
    }

    /// <summary>
    /// Marks the session as the user's, clearing any earlier sign-out, and pushes
    /// <see cref="ChannelNames.SignedInPush"/> to its tabs.
    /// </summary>
    public async Task SignInAsync(string sessionId, string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        await _sessions.SetUserIdAsync(sessionId, userId);
        await _push.SendToSessionAsync(sessionId, ChannelNames.SignedInPush, payload: null, DateTimeOffset.UtcNow.Add(LifecyclePushValidity));
    }

    /// <summary>
    /// Marks the session signed out and pushes <see cref="ChannelNames.SignedOutPush"/> to its tabs.
    /// Idempotent.
    /// </summary>
    /// <param name="sessionId">The session to sign out.</param>
    /// <param name="exceptConnectionId">A tab to leave out — the one that asked, which already knows.</param>
    public async Task SignOutAsync(string sessionId, string? exceptConnectionId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        await _sessions.SetLogoutAsync(sessionId);
        var expiry = DateTimeOffset.UtcNow.Add(LifecyclePushValidity);
        if (exceptConnectionId is null)
        {
            await _push.SendToSessionAsync(sessionId, ChannelNames.SignedOutPush, payload: null, expiry);
        }
        else
        {
            await _push.SendToSessionExceptAsync(sessionId, exceptConnectionId, ChannelNames.SignedOutPush, payload: null, expiry);
        }
    }
}
