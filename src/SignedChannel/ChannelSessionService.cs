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
    /// Marks the session as the user's and pushes <see cref="ChannelNames.SignedInPush"/> to its
    /// tabs — unless the session no longer exists or was signed out, in which case nothing is signed
    /// in and its tabs are told <see cref="ChannelNames.SignedOutPush"/> instead.
    /// </summary>
    /// <returns>Whether the session was signed in.</returns>
    /// <remarks>
    /// A signed-out session is ended for good (see <see cref="ChannelNames.SessionEndedCode"/>).
    /// Signing it in again would revive keys that were signed out and merge two sign-ins into one
    /// session; a browser that still holds it is told to drop it and start a new one.
    /// </remarks>
    public async Task<bool> SignInAsync(string sessionId, string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var session = await _sessions.GetByIdAsync(sessionId);
        if (session is null)
        {
            return false;
        }
        var expiry = DateTimeOffset.UtcNow.Add(LifecyclePushValidity);
        if (session.LogoutDateTimeOffset is not null)
        {
            await _push.SendToSessionAsync(sessionId, ChannelNames.SignedOutPush, payload: null, expiry);
            return false;
        }

        await _sessions.SetUserIdAsync(sessionId, userId);
        await _push.SendToSessionAsync(sessionId, ChannelNames.SignedInPush, payload: null, expiry);
        return true;
    }

    /// <summary>
    /// Signs the person out of the whole BROWSER the session is in: every still signed-in session
    /// of theirs that shares its cookie secret (one per tab), each told
    /// <see cref="ChannelNames.SignedOutPush"/>. What a user means by "sign out" — and what
    /// <see cref="ChannelNames.SignOut"/> does. Another person signed in elsewhere in the same
    /// browser is left alone.
    /// </summary>
    /// <param name="sessionId">Any session of the browser — usually the one asking.</param>
    /// <param name="exceptConnectionId">A tab of <paramref name="sessionId"/> to leave out — the one that asked, which already knows.</param>
    /// <returns>How many sessions were signed out.</returns>
    public async Task<int> SignOutBrowserAsync(string sessionId, string? exceptConnectionId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var sessionIds = await _sessions.GetSignedInSessionIdsOfBrowserAsync(sessionId);
        foreach (var id in sessionIds)
        {
            await SignOutAsync(id, id == sessionId ? exceptConnectionId : null);
        }
        // The asking session itself, even if it was already anonymous or signed out (idempotent).
        if (!sessionIds.Contains(sessionId))
        {
            await SignOutAsync(sessionId, exceptConnectionId);
        }
        return sessionIds.Count;
    }

    /// <summary>
    /// Marks the session signed out and pushes <see cref="ChannelNames.SignedOutPush"/> to its tabs.
    /// Idempotent. One session only — <see cref="SignOutBrowserAsync"/> is the user-facing sign-out.
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
