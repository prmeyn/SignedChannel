using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace SignedChannel.BuiltIn;

/// <summary>
/// The browser signs itself out: every signed-in session of this person in this browser (one per
/// tab) is tombstoned and told, the asking tab excepted — it already knows. The cookie secret
/// stays: it is the browser's, shared with any other sessions there. Anything else the application
/// ties to a sign-in is for its <see cref="IChannelSignOutObserver"/>. Idempotent, and allowed for
/// any session, since signing out an anonymous one is harmless.
/// </summary>
internal sealed class SignOutAction : ISecureWebBrowserAction<MessageWebBrowserRequestBase, MessageWebBrowserResponseBase>
{
    private readonly ChannelSessionService _sessions;
    private readonly IServiceProvider _serviceProvider;

    public SignOutAction(ChannelSessionService sessions, IServiceProvider serviceProvider)
    {
        _sessions = sessions;
        _serviceProvider = serviceProvider;
    }

    public WebBrowserActionsValidationResult ValidateFromWebBrowser(string sessionId, string? userId, string? latestDeviceId, MessageWebBrowserRequestBase? unusedRequest) =>
        new() { IsValid = !string.IsNullOrWhiteSpace(sessionId) };

    public Task<bool> HasAccess(HttpRequest unusedHttpRequest, string sessionId, string? userId, string? latestDeviceId, MessageWebBrowserRequestBase? unusedRequest) =>
        Task.FromResult(true);

    public async Task<MessageWebBrowserResponseBase> ProcessMessageFromWebBrowserAsync(
        HttpRequest httpRequest, string sessionId, string signalRConnectionId, string? userId, string? latestDeviceId,
        MessageWebBrowserRequestBase? unusedRequest, string jobId, CancellationToken cancellationToken)
    {
        await _sessions.SignOutBrowserAsync(sessionId, exceptConnectionId: signalRConnectionId);

        var observer = _serviceProvider.GetService<IChannelSignOutObserver>();
        if (observer is not null)
        {
            await observer.OnBrowserSignedOutAsync(new BrowserSignedOutContext(httpRequest, sessionId, userId, latestDeviceId));
        }

        return new MessageWebBrowserResponseBase();
    }
}
