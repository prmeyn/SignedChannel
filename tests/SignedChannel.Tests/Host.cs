using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;

// A minimal host application, as a project using the package would write it: its own action, its
// own custom instruction, and a sign-in that is the host's business.

namespace SignedChannel.Tests.Host.Orders.Actions.WebApp.Ship
{
    public sealed record ShipRequest : MessageWebBrowserRequestBase
    {
        [JsonPropertyName("orderId")]
        public required string OrderId { get; init; }
    }

    /// <summary>Tells the session's other tabs that an order shipped — a host-defined instruction.</summary>
    public sealed class ShipAction : ISecureWebBrowserAction<ShipRequest, MessageWebBrowserResponseBase>
    {
        private readonly ChannelPushService _push;

        public ShipAction(ChannelPushService push) => _push = push;

        public WebBrowserActionsValidationResult ValidateFromWebBrowser(string sessionId, string? userId, string? latestDeviceId, ShipRequest? request) =>
            new() { IsValid = !string.IsNullOrWhiteSpace(request?.OrderId) };

        public Task<bool> HasAccess(HttpRequest unusedHttpRequest, string sessionId, string? userId, string? latestDeviceId, ShipRequest? unusedRequest) =>
            Task.FromResult(true);

        public async Task<MessageWebBrowserResponseBase> ProcessMessageFromWebBrowserAsync(
            HttpRequest unusedHttpRequest, string sessionId, string signalRConnectionId, string? userId, string? latestDeviceId,
            ShipRequest? request, string jobId, CancellationToken cancellationToken)
        {
            await _push.PushInstructionAsync(sessionId, "Orders.Shipped", $$"""{"orderId":"{{request!.OrderId}}"}""",
                TimeSpan.FromMinutes(1), exceptConnectionId: signalRConnectionId);
            return new MessageWebBrowserResponseBase();
        }
    }
}

namespace SignedChannel.Tests.Host.Dev.Actions.WebApp.SignIn
{
    public sealed record SignInRequest : MessageWebBrowserRequestBase
    {
        [JsonPropertyName("userId")]
        public required string UserId { get; init; }
    }

    /// <summary>Stands in for whatever authentication the host really does.</summary>
    public sealed class SignInAction : ISecureWebBrowserAction<SignInRequest, MessageWebBrowserResponseBase>
    {
        private readonly ChannelSessionService _sessions;

        public SignInAction(ChannelSessionService sessions) => _sessions = sessions;

        public WebBrowserActionsValidationResult ValidateFromWebBrowser(string sessionId, string? userId, string? latestDeviceId, SignInRequest? request) =>
            new() { IsValid = !string.IsNullOrWhiteSpace(request?.UserId) };

        public Task<bool> HasAccess(HttpRequest unusedHttpRequest, string sessionId, string? userId, string? latestDeviceId, SignInRequest? unusedRequest) =>
            Task.FromResult(true);

        public async Task<MessageWebBrowserResponseBase> ProcessMessageFromWebBrowserAsync(
            HttpRequest unusedHttpRequest, string sessionId, string signalRConnectionId, string? userId, string? latestDeviceId,
            SignInRequest? request, string jobId, CancellationToken cancellationToken)
        {
            await _sessions.SignInAsync(sessionId, request!.UserId);
            return new MessageWebBrowserResponseBase();
        }
    }
}

namespace SignedChannel.Tests.BadHost.Channel.Actions.WebApp.Register
{
    /// <summary>A host action that would collide with the reserved <c>Channel.*</c> names.</summary>
    public sealed class RegisterAction : ISecureWebBrowserAction<MessageWebBrowserRequestBase, MessageWebBrowserResponseBase>
    {
        public WebBrowserActionsValidationResult ValidateFromWebBrowser(string sessionId, string? userId, string? latestDeviceId, MessageWebBrowserRequestBase? request) =>
            new() { IsValid = true };

        public Task<bool> HasAccess(HttpRequest unusedHttpRequest, string sessionId, string? userId, string? latestDeviceId, MessageWebBrowserRequestBase? unusedRequest) =>
            Task.FromResult(true);

        public Task<MessageWebBrowserResponseBase> ProcessMessageFromWebBrowserAsync(
            HttpRequest unusedHttpRequest, string sessionId, string signalRConnectionId, string? userId, string? latestDeviceId,
            MessageWebBrowserRequestBase? unusedRequest, string jobId, CancellationToken cancellationToken) =>
            Task.FromResult(new MessageWebBrowserResponseBase());
    }
}
