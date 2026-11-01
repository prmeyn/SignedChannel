using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using PublicKeyUtils.CryptoKeys;

namespace SignedChannel.Tests;

/// <summary>A push the fake hub saw, and where it was addressed.</summary>
public sealed record SentPush(string Target, PushMessage Message);

/// <summary>
/// Stands in for SignalR: records every push and every group join, so a test can read back what a
/// real browser would have received.
/// </summary>
public sealed class RecordingHubContext : IHubContext<PresenceHub>, IHubClients, IGroupManager
{
    public ConcurrentQueue<SentPush> Sent { get; } = new();

    public ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> GroupMembers { get; } = new();

    IHubClients IHubContext<PresenceHub>.Clients => this;

    IGroupManager IHubContext<PresenceHub>.Groups => this;

    IClientProxy IHubClients<IClientProxy>.All => Proxy("all");

    IClientProxy IHubClients<IClientProxy>.AllExcept(IReadOnlyList<string> excludedConnectionIds) => Proxy("all-except");

    IClientProxy IHubClients<IClientProxy>.Client(string connectionId) => Proxy($"connection:{connectionId}");

    IClientProxy IHubClients<IClientProxy>.Clients(IReadOnlyList<string> connectionIds) => Proxy("connections");

    IClientProxy IHubClients<IClientProxy>.Group(string groupName) => Proxy($"group:{groupName}");

    IClientProxy IHubClients<IClientProxy>.GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) =>
        Proxy($"group:{groupName}-except:{string.Join(",", excludedConnectionIds)}");

    IClientProxy IHubClients<IClientProxy>.Groups(IReadOnlyList<string> groupNames) => Proxy("groups");

    IClientProxy IHubClients<IClientProxy>.User(string userId) => Proxy($"user:{userId}");

    IClientProxy IHubClients<IClientProxy>.Users(IReadOnlyList<string> userIds) => Proxy("users");

    public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
    {
        GroupMembers.GetOrAdd(groupName, _ => new())[connectionId] = 0;
        return Task.CompletedTask;
    }

    public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
    {
        if (GroupMembers.TryGetValue(groupName, out var members))
        {
            members.TryRemove(connectionId, out _);
        }
        return Task.CompletedTask;
    }

    private RecordingProxy Proxy(string target) => new(this, target);

    private sealed class RecordingProxy(RecordingHubContext hub, string target) : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            Assert.Equal(ChannelPushService.ReceiveEncryptedMessageMethod, method);
            hub.Sent.Enqueue(new SentPush(target, (PushMessage)args[0]!));
            return Task.CompletedTask;
        }
    }
}

/// <summary>Session storage in a dictionary, honouring the store contract (sign-in clears the tombstone).</summary>
public sealed class InMemorySessionStore : IChannelSessionStore
{
    public ConcurrentDictionary<string, ChannelSessionRecord> Sessions { get; } = new();

    public Task<ChannelSessionRecord?> GetByIdAsync(string sessionId) =>
        Task.FromResult(Sessions.TryGetValue(sessionId, out var record) ? record : null);

    public Task CreateAsync(ChannelSessionRecord record)
    {
        Sessions[record.Id] = record;
        return Task.CompletedTask;
    }

    public Task AddConnectionIdAsync(string sessionId, string connectionId)
    {
        if (Sessions.TryGetValue(sessionId, out var record) && !record.ConnectionIds.Contains(connectionId))
        {
            record.ConnectionIds.Add(connectionId);
        }
        return Task.CompletedTask;
    }

    public Task<ChannelSessionRecord?> RemoveConnectionIdAsync(string connectionId)
    {
        var record = Sessions.Values.FirstOrDefault(r => r.ConnectionIds.Remove(connectionId));
        return Task.FromResult(record);
    }

    public Task PruneConnectionIdsAsync(string sessionId, IReadOnlyCollection<string> deadConnectionIds)
    {
        if (Sessions.TryGetValue(sessionId, out var record))
        {
            record.ConnectionIds.RemoveAll(deadConnectionIds.Contains);
        }
        return Task.CompletedTask;
    }

    public Task SetUserIdAsync(string sessionId, string userId)
    {
        // As MongoChannelSessionStore: a signed-out session is ended for good.
        var record = Sessions[sessionId];
        if (record.LogoutDateTimeOffset is null)
        {
            record.UserId = userId;
            record.LastActivityDateTimeOffset = DateTimeOffset.UtcNow;
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> GetSignedInSessionIdsOfBrowserAsync(string sessionId)
    {
        // As MongoChannelSessionStore: same cookie secret (browser) and same person, still signed in.
        if (!Sessions.TryGetValue(sessionId, out var session) || session.UserId is null)
        {
            return Task.FromResult<IReadOnlyList<string>>([]);
        }
        IReadOnlyList<string> ids = Sessions.Values
            .Where(s => s.CookieIdHash == session.CookieIdHash && s.UserId == session.UserId && s.LogoutDateTimeOffset is null)
            .Select(s => s.Id)
            .ToList();
        return Task.FromResult(ids);
    }

    public Task SetLogoutAsync(string sessionId) => SetLogoutAsync(sessionId, DateTimeOffset.UtcNow);

    public Task SetLogoutAsync(string sessionId, DateTimeOffset loggedOutAt)
    {
        // As MongoChannelSessionStore: the first stamp wins.
        Sessions[sessionId].LogoutDateTimeOffset ??= loggedOutAt;
        return Task.CompletedTask;
    }

    public Task TouchActivityAsync(string sessionId, DateTimeOffset now)
    {
        Sessions[sessionId].LastActivityDateTimeOffset = now;
        return Task.CompletedTask;
    }
}

/// <summary>
/// A real ASP.NET Core host running the channel on a test server, with SignalR replaced by
/// <see cref="RecordingHubContext"/>.
/// </summary>
public sealed class TestChannelHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private TestChannelHost(WebApplication app)
    {
        _app = app;
        Client = app.GetTestClient();
    }

    public HttpClient Client { get; }

    public IServiceProvider Services => _app.Services;

    public RecordingHubContext Hub => (RecordingHubContext)Services.GetRequiredService<IHubContext<PresenceHub>>();

    public InMemorySessionStore Sessions => (InMemorySessionStore)Services.GetRequiredService<IChannelSessionStore>();

    public static async Task<TestChannelHost> StartAsync(Action<IServiceCollection>? configureServices = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSignedChannel(options =>
        {
            options.ActionAssembly = typeof(TestChannelHost).Assembly;
            options.RootNamespace = "SignedChannel.Tests.Host";
        });
        builder.Services.AddSingleton<IChannelSessionStore, InMemorySessionStore>();
        builder.Services.AddSingleton<IHubContext<PresenceHub>, RecordingHubContext>();
        configureServices?.Invoke(builder.Services);

        var app = builder.Build();
        app.MapSignedChannel();
        await app.StartAsync();
        return new TestChannelHost(app);
    }

    /// <summary>A browser tab with fresh keys, registered as a new session.</summary>
    public async Task<TestBrowser> OpenSessionAsync()
    {
        var browser = new TestBrowser(this, TestBrowser.NewConnectionId());
        await browser.RegisterSessionAsync();
        return browser;
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}

/// <summary>
/// The browser half of the protocol, in C#: the same keys, envelopes and signatures the npm client
/// produces, so the tests drive the real endpoints rather than calling actions directly.
/// </summary>
public sealed class TestBrowser
{
    private const string HashAlgorithm = "SHA-384";

    private readonly TestChannelHost _host;
    private readonly ECDsa _signingKey;
    private readonly RSA _encryptionKey;
    /// <summary>The browser's cookies — shared by every tab and session of one browser, as a real one does.</summary>
    private readonly CookieJar _cookies;

    /// <summary>One browser's cookie store.</summary>
    public sealed class CookieJar
    {
        public string? Value { get; set; }
    }

    public TestBrowser(TestChannelHost host, string connectionId, TestBrowser? sameSessionAs = null, CookieJar? sameBrowserAs = null)
    {
        _host = host;
        ConnectionId = connectionId;
        _signingKey = sameSessionAs?._signingKey ?? ECDsa.Create(ECCurve.NamedCurves.nistP384);
        _encryptionKey = sameSessionAs?._encryptionKey ?? RSA.Create(2048);
        SessionId = sameSessionAs?.SessionId ?? string.Empty;
        _cookies = sameSessionAs?._cookies ?? sameBrowserAs ?? new CookieJar();

        // A live socket, as the hub would have recorded it on connect.
        host.Services.GetRequiredService<ConnectionRegistry>().Add(connectionId);
    }

    /// <summary>The <c>Sec-CH-UA</c> client hint every registration sends.</summary>
    public const string BrandsHeader = "\"Brave\";v=\"141\", \"Not?A_Brand\";v=\"8\", \"Chromium\";v=\"141\"";

    public string SessionId { get; private set; }

    public string ConnectionId { get; }

    public static string NewConnectionId() => Guid.NewGuid().ToString("N");

    /// <summary>A second tab of the same session, on its own connection.</summary>
    public TestBrowser OpenAnotherTab() => new(_host, NewConnectionId(), this);

    /// <summary>A new tab in the same browser: its own keys and session, the browser's cookies.</summary>
    public async Task<TestBrowser> OpenNewSessionInSameBrowserAsync()
    {
        var tab = new TestBrowser(_host, NewConnectionId(), sameBrowserAs: _cookies);
        await tab.RegisterSessionAsync();
        return tab;
    }

    /// <summary>The browser's cookie as the server last set it (null when none).</summary>
    public string? Cookie
    {
        get => _cookies.Value;
        set => _cookies.Value = value;
    }

    public async Task RegisterSessionAsync()
    {
        var ec = _signingKey.ExportParameters(includePrivateParameters: false);
        var verifyingKey = ChannelCrypto.Serialize(new SignVerifyPublicKey
        {
            Kty = "EC",
            Crv = "P-384",
            X = Base64Url(ec.Q.X!),
            Y = Base64Url(ec.Q.Y!),
            KeyOps = ["verify"]
        });

        var rsa = _encryptionKey.ExportParameters(includePrivateParameters: false);
        var encryptionKey = ChannelCrypto.Serialize(new EncryptDecryptPublicKey
        {
            Algorithm = "RSA-OAEP-256",
            E = Base64Url(rsa.Exponent!),
            N = Base64Url(rsa.Modulus!),
            KeyOps = ["encrypt"]
        });

        var signedBlob = ChannelCrypto.Serialize(new EncryptionPublicKeyWithTimestampRequest(encryptionKey, DateTimeOffset.UtcNow));
        using var register = new HttpRequestMessage(HttpMethod.Post, "/api/session/register")
        {
            Content = JsonContent.Create(new SessionPublicKeysRegistrationRequest(
                WebBrowserId: "browser-1",
                SignalRConnectionId: ConnectionId,
                SelectedLanguageIsoCode: "en",
                VerifyingPublicKeyBase64: verifyingKey,
                EncryptionPublicKeyRequestWithTimestampAsBase64: signedBlob,
                EncryptionPublicKeySignatureAsBase64: Sign(signedBlob),
                HashAlgorithm: HashAlgorithm))
        };
        if (_cookies.Value is not null)
        {
            register.Headers.Add("Cookie", _cookies.Value);
        }
        // What a Chromium browser sends on its own fetches (Brave's user-agent is Chrome's).
        register.Headers.TryAddWithoutValidation("Sec-CH-UA", BrandsHeader);
        var response = await _host.Client.SendAsync(register);
        response.EnsureSuccessStatusCode();
        CaptureCookie(response);

        var body = await response.Content.ReadFromJsonAsync<SessionPublicKeysRegistrationResponse>();
        SessionId = Decrypt(body!.EncryptedSessionIdAsBase64);
    }

    /// <summary>Sends a signed action; returns the status code and the response body as JSON.</summary>
    public async Task<(int Status, JsonElement Body)> SendAsync(string actionName, object? payload = null)
    {
        var payloadWithPage = payload is null
            ? new Dictionary<string, object?>()
            : JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(payload))!;
        payloadWithPage["currentUrl"] = "https://app.test/page";

        var message = ChannelCrypto.Serialize(new WebBrowserMessagePayloadRequest(
            SessionId, ConnectionId, actionName, DateTimeOffset.UtcNow,
            Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payloadWithPage)))));

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/action")
        {
            Content = JsonContent.Create(new SignedWebBrowserPayloadRequest(message, Sign(message), HashAlgorithm))
        };
        if (_cookies.Value is not null)
        {
            request.Headers.Add("Cookie", _cookies.Value);
        }

        var response = await _host.Client.SendAsync(request);
        CaptureCookie(response);
        var text = await response.Content.ReadAsStringAsync();
        var body = string.IsNullOrWhiteSpace(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
        return ((int)response.StatusCode, body);
    }

    /// <summary>Runs the connection handshake: Register, answer the challenge, confirm the group join.</summary>
    public async Task<JsonElement> ConnectAsync()
    {
        var (status, registered) = await SendAsync(ChannelNames.Register);
        Assert.Equal(202, status);

        var challenge = TakePush($"connection:{ConnectionId}", ChannelNames.ConnectionChallengePush);
        var (verifyStatus, verified) = await SendAsync(ChannelNames.VerifyConnection, new { token = Decrypt(challenge.EncryptedPayloadAsBase64) });
        Assert.Equal(202, verifyStatus);
        Assert.True(verified.GetProperty("verified").GetBoolean());
        return registered;
    }

    /// <summary>Removes and returns the latest push to the target with the given name.</summary>
    public PushMessage TakePush(string target, string actionName)
    {
        var match = _host.Hub.Sent.LastOrDefault(p => p.Target == target && p.Message.ActionName == actionName);
        Assert.NotNull(match);
        return match.Message;
    }

    /// <summary>Downloads the instruction an <see cref="ChannelNames.InstructionPush"/> points at.</summary>
    public async Task<JsonElement> DownloadAsync(PushMessage instructionPush)
    {
        Assert.Equal(ChannelNames.InstructionPush, instructionPush.ActionName);
        var (status, body) = await SendAsync(ChannelNames.DownloadInstruction, new { pointer = Decrypt(instructionPush.EncryptedPayloadAsBase64) });
        Assert.Equal(202, status);
        return body;
    }

    public string Decrypt(string encryptedBase64) =>
        Encoding.UTF8.GetString(_encryptionKey.Decrypt(Convert.FromBase64String(encryptedBase64), RSAEncryptionPadding.OaepSHA256));

    public string Sign(string message) =>
        Convert.ToBase64String(_signingKey.SignData(Encoding.UTF8.GetBytes(message), HashAlgorithmName.SHA384));

    private void CaptureCookie(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            foreach (var cookie in cookies)
            {
                var pair = cookie.Split(';')[0];
                _cookies.Value = pair.EndsWith('=') ? null : pair;
            }
        }
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
