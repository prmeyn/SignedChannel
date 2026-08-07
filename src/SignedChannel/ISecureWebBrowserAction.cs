using Microsoft.AspNetCore.Http;

namespace SignedChannel;

/// <summary>
/// A browser action: one unit of business logic, reached over the signed channel instead of an
/// HTTP endpoint of its own.
/// <para>
/// Actions are resolved from the wire action name by namespace convention — <c>"A.B"</c> maps to
/// <c>{RootNamespace}.A.Actions.WebApp.B.BAction</c> — so adding one is adding a class, with no
/// route registration and no per-action DI wiring. The assembly and root namespace to scan are
/// supplied by the host application.
/// </para>
/// <para>
/// The three members run in order and separate three different questions: is the request
/// well-formed (<see cref="ValidateFromWebBrowser"/>), is this caller allowed to make it
/// (<see cref="HasAccess"/>), and what does it do (<see cref="ProcessMessageFromWebBrowserAsync"/>).
/// Keeping them apart is what lets the dispatcher answer a malformed request and an unauthorised
/// one differently without every action re-implementing that.
/// </para>
/// </summary>
/// <typeparam name="TRequest">The action's request payload.</typeparam>
/// <typeparam name="TResponse">The action's response payload.</typeparam>
public interface ISecureWebBrowserAction<TRequest, TResponse>
    where TRequest : MessageWebBrowserRequestBase
    where TResponse : MessageWebBrowserResponseBase
{
    /// <summary>
    /// Shape and range checks on the request. Runs first; a false result is answered as a
    /// validation failure without the action being invoked.
    /// </summary>
    WebBrowserActionsValidationResult ValidateFromWebBrowser(string sessionId, string? userId, string? latestDeviceId, TRequest? request);

    /// <summary>
    /// Whether this session/user may perform the action. Runs after validation and before
    /// processing; a false result is answered as a rejection without the action being invoked.
    /// </summary>
    Task<bool> HasAccess(HttpRequest httpRequest, string sessionId, string? userId, string? latestDeviceId, TRequest? request);

    /// <summary>
    /// Performs the action. The jobId/cancellationToken pair is infrastructure for long-running
    /// work that reports <see cref="MessageWebBrowserResponseBase.PercentageComplete"/> below 100;
    /// a synchronous action can ignore both.
    /// </summary>
    Task<TResponse> ProcessMessageFromWebBrowserAsync(
        HttpRequest httpRequest,
        string sessionId,
        string signalRConnectionId,
        string? userId,
        string? latestDeviceId,
        TRequest? request,
        string jobId,
        CancellationToken cancellationToken);
}
