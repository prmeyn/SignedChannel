using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace SignedChannel.BuiltIn;

/// <summary>
/// The browser signs itself out: the session is tombstoned, its other tabs are told (the asking
/// one already knows), and the session cookie is dropped. Anything else the application ties to a
/// sign-in is for its <see cref="IChannelSignOutObserver"/>. Idempotent, and allowed for any
/// session, since signing out an anonymous one is harmless.
/// </summary>
internal sealed class SignOutAction : ISecureWebBrowserAction<MessageWebBrowserRequestBase, MessageWebBrowserResponseBase>
{
    private readonly ChannelSessionService _sessions;
    private readonly SignedChannelOptions _options;
    private readonly IServiceProvider _serviceProvider;

    public SignOutAction(ChannelSessionService sessions, IOptions<SignedChannelOptions> options, IServiceProvider serviceProvider)
    {
        _sessions = sessions;
        _options = options.Value;
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
        await _sessions.SignOutAsync(sessionId, exceptConnectionId: signalRConnectionId);
        httpRequest.HttpContext.Response.Cookies.Delete(_options.SessionCookieName);

        var observer = _serviceProvider.GetService<IChannelSignOutObserver>();
        if (observer is not null)
        {
            await observer.OnBrowserSignedOutAsync(new BrowserSignedOutContext(httpRequest, sessionId, userId, latestDeviceId));
        }

        return new MessageWebBrowserResponseBase();
    }
}
