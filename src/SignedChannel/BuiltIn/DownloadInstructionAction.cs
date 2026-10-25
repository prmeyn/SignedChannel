using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;

namespace SignedChannel.BuiltIn;

/// <summary>Request of <see cref="ChannelNames.DownloadInstruction"/>.</summary>
public sealed record DownloadInstructionRequest : MessageWebBrowserRequestBase
{
    /// <summary>The pointer that arrived, decrypted, with <see cref="ChannelNames.InstructionPush"/>.</summary>
    [JsonPropertyName("pointer")]
    public required string Pointer { get; init; }
}

/// <summary>Answer to <see cref="ChannelNames.DownloadInstruction"/>.</summary>
public sealed record DownloadInstructionResponse : MessageWebBrowserResponseBase
{
    /// <summary>The instruction's name, or null if the pointer was spent, expired or not this session's.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>The instruction's payload, or null.</summary>
    [JsonPropertyName("payload")]
    public string? Payload { get; init; }
}

/// <summary>
/// Exchanges an instruction pointer for the instruction. Single-use: every tab of a session is
/// pushed its own pointer, and each pointer is good for exactly one download.
/// </summary>
internal sealed class DownloadInstructionAction : ISecureWebBrowserAction<DownloadInstructionRequest, DownloadInstructionResponse>
{
    private readonly IChannelInstructionStore _instructions;

    public DownloadInstructionAction(IChannelInstructionStore instructions) => _instructions = instructions;

    public WebBrowserActionsValidationResult ValidateFromWebBrowser(string sessionId, string? userId, string? latestDeviceId, DownloadInstructionRequest? request) =>
        new() { IsValid = !string.IsNullOrWhiteSpace(request?.Pointer) };

    public Task<bool> HasAccess(HttpRequest unusedHttpRequest, string sessionId, string? userId, string? latestDeviceId, DownloadInstructionRequest? unusedRequest) =>
        Task.FromResult(true);

    public async Task<DownloadInstructionResponse> ProcessMessageFromWebBrowserAsync(
        HttpRequest unusedHttpRequest, string sessionId, string signalRConnectionId, string? userId, string? latestDeviceId,
        DownloadInstructionRequest? request, string jobId, CancellationToken cancellationToken)
    {
        var instruction = await _instructions.DownloadAsync(sessionId, request!.Pointer);
        return new DownloadInstructionResponse { Name = instruction?.Name, Payload = instruction?.Payload };
    }
}
