namespace SignedChannel;

/// <summary>
/// Persistence the channel itself needs. Deliberately narrow: everything here is called by the
/// handshake, the dispatcher or the presence hub, and nothing else belongs.
/// <para>
/// An application with richer per-session needs — listing a user's sessions, remembering
/// half-finished sign-in state — should put those on its own interface rather than widening this
/// one. <c>SignedChannel.MongoDb</c> ships an implementation that can be inherited for exactly that.
/// </para>
/// </summary>
public interface IChannelSessionStore
{
    /// <summary>The session, or null if no such session exists.</summary>
    Task<ChannelSessionRecord?> GetByIdAsync(string sessionId);

    /// <summary>Stores a newly registered session.</summary>
    Task CreateAsync(ChannelSessionRecord record);

    /// <summary>Attaches a SignalR connection to the session. Idempotent.</summary>
    Task AddConnectionIdAsync(string sessionId, string connectionId);

    /// <summary>
    /// Detaches a closed connection from whichever session holds it, and returns that session so
    /// the caller can react to a tab closing. Null when no session held it.
    /// </summary>
    Task<ChannelSessionRecord?> RemoveConnectionIdAsync(string connectionId);

    /// <summary>
    /// Drops connection ids now known to be dead. A process restart leaves recorded connections
    /// that will never receive a disconnect event, so liveness has to be healed rather than
    /// trusted.
    /// </summary>
    Task PruneConnectionIdsAsync(string sessionId, IReadOnlyCollection<string> deadConnectionIds);

    /// <summary>
    /// Marks the session as belonging to a user — this is the sign-in. Applications call it through
    /// <see cref="ChannelSessionService.SignInAsync"/>, which also tells the session's tabs.
    /// <para>
    /// Implementations must also start the idle clock. They must NOT clear
    /// <see cref="ChannelSessionRecord.LogoutDateTimeOffset"/>: a signed-out session is ended for good
    /// (<see cref="ChannelNames.SessionEndedCode"/>), and <see cref="ChannelSessionService.SignInAsync"/>
    /// never calls this for one. Before 10.3 this method cleared it, so that a browser reusing its
    /// session id after a sign-out could sign in again; browsers now start a new session instead.
    /// </para>
    /// </summary>
    Task SetUserIdAsync(string sessionId, string userId);

    /// <summary>Marks the session logged out. Idempotent.</summary>
    Task SetLogoutAsync(string sessionId);

    /// <summary>Records activity, sliding the idle-expiry window.</summary>
    Task TouchActivityAsync(string sessionId, DateTimeOffset now);

    /// <summary>
    /// The still signed-in sessions of the same person in the same browser as
    /// <paramref name="sessionId"/> (itself included, if signed in): same
    /// <see cref="ChannelSessionRecord.CookieIdHash"/> — one secret per browser — and same
    /// <see cref="ChannelSessionRecord.UserId"/>, with no sign-out. What
    /// <see cref="ChannelSessionService.SignOutBrowserAsync"/> ends.
    /// </summary>
    /// <remarks>
    /// The default answers with the session alone, so a store written before 10.4 keeps compiling
    /// and signs out one session as it always did; override it to sign out the whole browser.
    /// </remarks>
    async Task<IReadOnlyList<string>> GetSignedInSessionIdsOfBrowserAsync(string sessionId)
    {
        var session = await GetByIdAsync(sessionId);
        return session?.UserId is not null && session.LogoutDateTimeOffset is null ? [sessionId] : [];
    }
}
