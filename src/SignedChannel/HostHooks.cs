using Microsoft.AspNetCore.Http;

namespace SignedChannel;

/// <summary>
/// Resolves the device that established a session's identity.
/// <para>
/// Optional. The resulting value is handed to every action as <c>latestDeviceId</c> and is opaque
/// to the channel — it exists for applications where sign-in is performed by a companion device
/// and an action needs to know which one. Leave it unregistered and actions receive null.
/// </para>
/// </summary>
public interface ISessionDeviceResolver
{
    /// <summary>The device id currently associated with this user, or null if there is none.</summary>
    Task<string?> GetLatestDeviceIdAsync(string userId);
}

/// <summary>
/// What the application is told about a freshly registered session.
/// </summary>
/// <param name="SessionId">The new session's id.</param>
/// <param name="WebBrowserId">The browser it belongs to.</param>
/// <param name="SignalRConnectionId">The connection it registered with.</param>
/// <param name="PreferredLanguageIsoCode">The language it asked for.</param>
/// <param name="UserAgent">The browser's user-agent header, if it sent one.</param>
/// <param name="ApiVersion">Channel version stamped on the session.</param>
/// <param name="UserAgentClientHint">
/// The browser's <c>Sec-CH-UA</c> client hint, if it sent one — e.g.
/// <c>"Brave";v="141", "Not?A_Brand";v="8", "Chromium";v="141"</c>. Chromium browsers send it, Firefox
/// and Safari do not. It names browsers whose user-agent is Chrome's word for word, such as Brave.
/// </param>
public sealed record SessionRegisteredContext(
    string SessionId,
    string WebBrowserId,
    string SignalRConnectionId,
    string PreferredLanguageIsoCode,
    string? UserAgent,
    string ApiVersion,
    string? UserAgentClientHint = null);

/// <summary>
/// Notified when a session is registered.
/// <para>
/// Optional. This is the hook for per-browser bookkeeping the channel has no opinion about —
/// remembering a browser's name across sessions, binding a connection row that survives a restart.
/// Awaited as part of the handshake, so keep it quick; a throw fails the registration.
/// </para>
/// </summary>
public interface IChannelSessionObserver
{
    /// <summary>Called after validation and before the session record is created.</summary>
    Task OnSessionRegisteredAsync(SessionRegisteredContext context);
}

/// <summary>
/// What the application is told when a browser signs itself out.
/// </summary>
/// <param name="HttpRequest">The sign-out request, so the host can clear its own cookies on the response.</param>
/// <param name="SessionId">The session that signed out.</param>
/// <param name="UserId">The user it belonged to, or null if it was not signed in.</param>
/// <param name="LatestDeviceId">From <see cref="ISessionDeviceResolver"/>, when one is registered.</param>
public sealed record BrowserSignedOutContext(
    HttpRequest HttpRequest,
    string SessionId,
    string? UserId,
    string? LatestDeviceId);

/// <summary>
/// Notified when a browser signs out through <see cref="ChannelNames.SignOut"/>.
/// <para>
/// Optional. The channel has already tombstoned the session, told its other tabs and dropped its
/// own cookie; this is for whatever else the application ties to a sign-in — its own auth cookies,
/// a companion device's session list. Awaited, so cookie changes land on the response; a throw
/// fails the request after the sign-out itself has happened.
/// </para>
/// </summary>
public interface IChannelSignOutObserver
{
    /// <summary>Called after the session has been signed out.</summary>
    Task OnBrowserSignedOutAsync(BrowserSignedOutContext context);
}

/// <summary>
/// One completed action, described for the audit trail.
/// </summary>
/// <param name="SessionId">Session that made the request.</param>
/// <param name="UserId">Application user id, or null if the session was not signed in.</param>
/// <param name="ActionName">Wire name of the action.</param>
/// <param name="JobId">Identifier of this invocation.</param>
/// <param name="RequestAsBase64">The request payload exactly as received, or null when there was none.</param>
/// <param name="Response">The response object, before serialization.</param>
/// <param name="CurrentUrl">Page the request was made from, when the request carried it.</param>
/// <param name="ReferrerUrl">Referring page, when the request carried one.</param>
/// <param name="HttpRequest">The live request, for anything else the recorder needs — remote IP, headers.</param>
/// <param name="StartedAt">When processing began.</param>
/// <param name="CompletedAt">When it finished.</param>
public sealed record ActionCompletedContext(
    string SessionId,
    string? UserId,
    string ActionName,
    string JobId,
    string? RequestAsBase64,
    MessageWebBrowserResponseBase Response,
    string? CurrentUrl,
    string? ReferrerUrl,
    HttpRequest HttpRequest,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt);

/// <summary>
/// Records completed actions.
/// <para>
/// Optional. Invoked detached from the response, so it cannot slow an action down — and equally,
/// nothing observes the returned task. Swallow errors: an action must not appear to fail because
/// its audit row did not land.
/// </para>
/// <para>
/// Note that <see cref="ActionCompletedContext.RequestAsBase64"/> and
/// <see cref="ActionCompletedContext.Response"/> are the real request and response bodies. Anything
/// that persists them is storing whatever those contain, which is a retention decision worth making
/// deliberately.
/// </para>
/// </summary>
public interface IChannelActivityRecorder
{
    /// <summary>Called after the response has been produced.</summary>
    Task RecordAsync(ActionCompletedContext context);
}
