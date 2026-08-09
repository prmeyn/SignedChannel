namespace SignedChannel;

/// <summary>
/// Optional hook fired when a signed-in session gains or loses a connection.
/// <para>
/// This exists so the channel can stay out of the notification business. The motivating case is a
/// companion app showing "this account is open in 3 tabs" — that count is only live if something
/// tells the app when a tab closes, but *how* it is told (push service, websocket, email) is the
/// application's concern, not the protocol's.
/// </para>
/// <para>
/// Register an implementation to receive the callbacks; leave it unregistered and the channel
/// simply does not make them. Implementations are invoked detached from the request and must not
/// throw — see the remarks on each member.
/// </para>
/// </summary>
public interface IConnectionChangeNotifier
{
    /// <summary>
    /// A signed-in session lost a connection, typically a closed tab or a navigation away.
    /// <para>
    /// Called after the connection has already been removed from the session, so a slow or failing
    /// implementation cannot hold up the disconnect. Best-effort by construction: nothing observes
    /// the returned task, so swallow errors rather than throwing.
    /// </para>
    /// </summary>
    /// <param name="userId">The application's own opaque user id from the session.</param>
    /// <param name="sessionId">The session that lost the connection.</param>
    Task OnSessionConnectionChangedAsync(string userId, string sessionId);
}
