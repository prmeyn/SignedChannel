using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace SignedChannel.Tests;

public sealed class HandshakeTests
{
    [Fact]
    public async Task Connection_joins_its_session_group_only_after_answering_the_challenge()
    {
        await using var host = await TestChannelHost.StartAsync();
        var tab = await host.OpenSessionAsync();

        var (status, registered) = await tab.SendAsync(ChannelNames.Register);
        Assert.Equal(202, status);
        Assert.False(registered.GetProperty("isSignedIn").GetBoolean());
        Assert.False(host.Hub.GroupMembers.ContainsKey(tab.SessionId));

        var challenge = tab.TakePush($"connection:{tab.ConnectionId}", ChannelNames.ConnectionChallengePush);
        var (_, verified) = await tab.SendAsync(ChannelNames.VerifyConnection, new { token = tab.Decrypt(challenge.EncryptedPayloadAsBase64) });

        Assert.True(verified.GetProperty("verified").GetBoolean());
        Assert.Contains(tab.ConnectionId, host.Hub.GroupMembers[tab.SessionId].Keys);
        tab.TakePush($"group:{tab.SessionId}", ChannelNames.ConnectionVerifiedPush);
    }

    [Fact]
    public async Task A_wrong_challenge_answer_is_refused_and_burns_the_token()
    {
        await using var host = await TestChannelHost.StartAsync();
        var tab = await host.OpenSessionAsync();
        await tab.SendAsync(ChannelNames.Register);
        var challenge = tab.TakePush($"connection:{tab.ConnectionId}", ChannelNames.ConnectionChallengePush);

        var (_, wrong) = await tab.SendAsync(ChannelNames.VerifyConnection, new { token = "not-the-token" });
        var (_, late) = await tab.SendAsync(ChannelNames.VerifyConnection, new { token = tab.Decrypt(challenge.EncryptedPayloadAsBase64) });

        Assert.False(wrong.GetProperty("verified").GetBoolean());
        Assert.False(late.GetProperty("verified").GetBoolean());
        Assert.False(host.Hub.GroupMembers.ContainsKey(tab.SessionId));
    }

    [Fact]
    public async Task A_connection_that_died_while_registering_is_not_kept()
    {
        var observer = new RecordingPresenceObserver();
        await using var host = await TestChannelHost.StartAsync(s => s.AddSingleton<IPresenceObserver>(observer));
        var tab = await host.OpenSessionAsync();
        host.Services.GetRequiredService<ConnectionRegistry>().Remove(tab.ConnectionId);

        await tab.SendAsync(ChannelNames.Register);

        Assert.DoesNotContain(tab.ConnectionId, host.Sessions.Sessions[tab.SessionId].ConnectionIds);
        Assert.Equal([$"bound:{tab.ConnectionId}", $"disconnected:{tab.ConnectionId}"], observer.Events);
        Assert.DoesNotContain(host.Hub.Sent, p => p.Message.ActionName == ChannelNames.ConnectionChallengePush);
    }

    [Fact]
    public async Task A_signed_in_session_gaining_a_tab_notifies_the_host()
    {
        var notifier = new RecordingConnectionChangeNotifier();
        await using var host = await TestChannelHost.StartAsync(s => s.AddSingleton<IConnectionChangeNotifier>(notifier));
        var tab = await host.OpenSessionAsync();
        await tab.ConnectAsync();
        Assert.Empty(notifier.Calls);

        await tab.SendAsync("Dev.SignIn", new { userId = "user-1" });
        var second = tab.OpenAnotherTab();
        await second.ConnectAsync();

        Assert.Equal([("user-1", tab.SessionId)], notifier.Calls);
    }

    [Fact]
    public void A_host_action_under_the_reserved_prefix_fails_startup()
    {
        var options = new SignedChannelOptions
        {
            ActionAssembly = typeof(HandshakeTests).Assembly,
            RootNamespace = "SignedChannel.Tests.BadHost"
        };

        var error = Assert.Throws<InvalidOperationException>(() => ActionResolver.ThrowIfHostDefinesReservedActions(options));
        Assert.Contains("SignedChannel.Tests.BadHost.Channel.Actions.WebApp.Register.RegisterAction", error.Message);

        options.RootNamespace = "SignedChannel.Tests.Host";
        ActionResolver.ThrowIfHostDefinesReservedActions(options);
    }

    [Fact]
    public void Reserved_names_resolve_only_to_built_in_actions()
    {
        var resolver = new ActionResolver(Options.Create(new SignedChannelOptions
        {
            ActionAssembly = typeof(HandshakeTests).Assembly,
            RootNamespace = "SignedChannel.Tests.BadHost"
        }));

        Assert.Equal(typeof(ChannelPushService).Assembly, resolver.Resolve(ChannelNames.Register)!.Type.Assembly);
        Assert.Null(resolver.Resolve("Channel.Unknown"));
    }
}

public sealed class InstructionTests
{
    [Fact]
    public async Task A_custom_instruction_reaches_every_other_tab_of_the_session()
    {
        await using var host = await TestChannelHost.StartAsync();
        var actingTab = await host.OpenSessionAsync();
        await actingTab.ConnectAsync();
        var secondTab = actingTab.OpenAnotherTab();
        await secondTab.ConnectAsync();
        var thirdTab = actingTab.OpenAnotherTab();
        await thirdTab.ConnectAsync();

        var (status, _) = await actingTab.SendAsync("Orders.Ship", new { orderId = "o-42" });
        Assert.Equal(202, status);

        // One pointer goes to the session's group, minus the tab that asked. Every tab that receives
        // it must be able to collect the instruction — not just whichever asks first.
        var push = actingTab.TakePush($"group:{actingTab.SessionId}-except:{actingTab.ConnectionId}", ChannelNames.InstructionPush);
        foreach (var tab in new[] { secondTab, thirdTab })
        {
            var instruction = await tab.DownloadAsync(push);
            Assert.Equal("Orders.Shipped", instruction.GetProperty("name").GetString());
            Assert.Equal("""{"orderId":"o-42"}""", instruction.GetProperty("payload").GetString());
        }
    }

    [Fact]
    public async Task Another_session_cannot_collect_an_instruction()
    {
        await using var host = await TestChannelHost.StartAsync();
        var owner = await host.OpenSessionAsync();
        var stranger = await host.OpenSessionAsync();
        var store = host.Services.GetRequiredService<IChannelInstructionStore>();

        var pointer = await store.StoreAsync(owner.SessionId, new ChannelInstruction("Secret", "x"), TimeSpan.FromMinutes(1));

        var (_, stolen) = await stranger.SendAsync(ChannelNames.DownloadInstruction, new { pointer });
        var (_, collected) = await owner.SendAsync(ChannelNames.DownloadInstruction, new { pointer });

        Assert.Null(stolen.GetProperty("name").GetString());
        Assert.Equal("Secret", collected.GetProperty("name").GetString());
    }

    [Fact]
    public async Task An_expired_or_malformed_pointer_yields_nothing()
    {
        var store = new InMemoryChannelInstructionStore();
        var pointer = await store.StoreAsync("s", new ChannelInstruction("Late", null), TimeSpan.FromMilliseconds(-1));

        Assert.Null(await store.DownloadAsync("s", pointer));
        Assert.Null(await store.DownloadAsync("s", "no-separator"));
        Assert.Null(await store.DownloadAsync("s", "_"));
    }

    [Fact]
    public async Task A_tampered_token_is_refused()
    {
        var store = new InMemoryChannelInstructionStore();
        var pointer = await store.StoreAsync("s", new ChannelInstruction("X", null), TimeSpan.FromMinutes(1));
        Assert.True(ChannelInstructionPointer.TryParse(pointer, out var messageId, out _));

        Assert.Null(await store.DownloadAsync("s", ChannelInstructionPointer.Format(messageId, "forged")));
    }
}

public sealed class SignInOutTests
{
    [Fact]
    public async Task Signing_in_a_fresh_session_marks_it_and_tells_the_tabs()
    {
        await using var host = await TestChannelHost.StartAsync();
        var tab = await host.OpenSessionAsync();
        await tab.ConnectAsync();

        var signedIn = await host.Services.GetRequiredService<ChannelSessionService>().SignInAsync(tab.SessionId, "user-1");

        Assert.True(signedIn);
        Assert.Equal("user-1", host.Sessions.Sessions[tab.SessionId].UserId);
        tab.TakePush($"group:{tab.SessionId}", ChannelNames.SignedInPush);
    }

    /// <summary>
    /// A signed-out session is ended for good. Signing it in again — what a tab that missed the
    /// sign-out push used to do — would revive keys that were signed out and merge two sign-ins.
    /// </summary>
    [Fact]
    public async Task Signing_in_an_ended_session_is_refused_and_its_tabs_are_told_to_leave()
    {
        await using var host = await TestChannelHost.StartAsync();
        var tab = await host.OpenSessionAsync();
        await tab.ConnectAsync();
        await tab.SendAsync("Dev.SignIn", new { userId = "user-1" });
        await host.Sessions.SetLogoutAsync(tab.SessionId);

        var signedIn = await host.Services.GetRequiredService<ChannelSessionService>().SignInAsync(tab.SessionId, "user-2");

        Assert.False(signedIn);
        var session = host.Sessions.Sessions[tab.SessionId];
        Assert.Equal("user-1", session.UserId);
        Assert.NotNull(session.LogoutDateTimeOffset);
        tab.TakePush($"group:{tab.SessionId}", ChannelNames.SignedOutPush);
    }

    [Fact]
    public async Task An_ended_session_runs_nothing_but_its_own_sign_out()
    {
        await using var host = await TestChannelHost.StartAsync();
        var tab = await host.OpenSessionAsync();
        await tab.ConnectAsync();
        await tab.SendAsync("Dev.SignIn", new { userId = "user-1" });
        await host.Services.GetRequiredService<ChannelSessionService>().SignOutAsync(tab.SessionId);

        var (keepAliveStatus, keepAlive) = await tab.SendAsync(ChannelNames.KeepAlive);
        var (signInStatus, signIn) = await tab.SendAsync("Dev.SignIn", new { userId = "user-1" });
        var (signOutStatus, _) = await tab.SendAsync(ChannelNames.SignOut);

        Assert.Equal((401, ChannelNames.SessionEndedCode), (keepAliveStatus, keepAlive.GetProperty("code").GetString()));
        Assert.Equal((401, ChannelNames.SessionEndedCode), (signInStatus, signIn.GetProperty("code").GetString()));
        Assert.Equal(202, signOutStatus);
        Assert.NotNull(host.Sessions.Sessions[tab.SessionId].LogoutDateTimeOffset);
    }

    [Fact]
    public async Task After_a_sign_out_new_keys_sign_in_as_a_new_session()
    {
        await using var host = await TestChannelHost.StartAsync();
        var old = await host.OpenSessionAsync();
        await old.ConnectAsync();
        await old.SendAsync("Dev.SignIn", new { userId = "user-1" });
        await old.SendAsync(ChannelNames.SignOut);

        var fresh = await host.OpenSessionAsync();
        await fresh.ConnectAsync();
        var (status, _) = await fresh.SendAsync("Dev.SignIn", new { userId = "user-1" });

        Assert.Equal(202, status);
        Assert.NotEqual(old.SessionId, fresh.SessionId);
        Assert.Equal("user-1", host.Sessions.Sessions[fresh.SessionId].UserId);
        Assert.Null(host.Sessions.Sessions[fresh.SessionId].LogoutDateTimeOffset);
    }

    [Fact]
    public async Task A_signed_in_tab_registers_as_signed_in_and_keep_alive_returns_the_expiry()
    {
        await using var host = await TestChannelHost.StartAsync();
        var tab = await host.OpenSessionAsync();
        await tab.ConnectAsync();
        await tab.SendAsync("Dev.SignIn", new { userId = "user-1" });

        var second = tab.OpenAnotherTab();
        var registered = await second.ConnectAsync();
        var (status, keptAlive) = await tab.SendAsync(ChannelNames.KeepAlive);

        Assert.True(registered.GetProperty("isSignedIn").GetBoolean());
        Assert.True(registered.GetProperty("warningSeconds").GetInt32() > 0);
        Assert.Equal(202, status);
        Assert.True(keptAlive.GetProperty("sessionExpiresAtUtc").GetDateTimeOffset() > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Signing_out_tombstones_the_session_tells_the_other_tabs_and_calls_the_observer()
    {
        var observer = new RecordingSignOutObserver();
        await using var host = await TestChannelHost.StartAsync(s => s.AddSingleton<IChannelSignOutObserver>(observer));
        var tab = await host.OpenSessionAsync();
        await tab.ConnectAsync();
        await tab.SendAsync("Dev.SignIn", new { userId = "user-1" });

        var (status, _) = await tab.SendAsync(ChannelNames.SignOut);

        Assert.Equal(202, status);
        Assert.NotNull(host.Sessions.Sessions[tab.SessionId].LogoutDateTimeOffset);
        tab.TakePush($"group:{tab.SessionId}-except:{tab.ConnectionId}", ChannelNames.SignedOutPush);
        var context = Assert.Single(observer.Contexts);
        Assert.Equal((tab.SessionId, "user-1"), (context.SessionId, context.UserId));

        // Signed out is ended: another tab still holding the session's keys cannot even register.
        var (registerStatus, registered) = await tab.OpenAnotherTab().SendAsync(ChannelNames.Register);
        Assert.Equal((401, ChannelNames.SessionEndedCode), (registerStatus, registered.GetProperty("code").GetString()));
    }
}

public sealed class TokenStoreTests
{
    [Fact]
    public async Task A_token_validates_once()
    {
        var store = new InMemoryChannelTokenStore();
        var token = await store.GenerateAsync("id", TimeSpan.FromMinutes(1));

        Assert.True(await store.ConsumeAndValidateAsync("id", token));
        Assert.False(await store.ConsumeAndValidateAsync("id", token));
    }

    [Fact]
    public async Task A_new_token_replaces_the_old_one()
    {
        var store = new InMemoryChannelTokenStore();
        var first = await store.GenerateAsync("id", TimeSpan.FromMinutes(1));
        var second = await store.GenerateAsync("id", TimeSpan.FromMinutes(1));

        Assert.False(await store.ConsumeAndValidateAsync("id", first));
        Assert.False(await store.ConsumeAndValidateAsync("id", second));
    }
}

internal sealed class RecordingPresenceObserver : IPresenceObserver
{
    public List<string> Events { get; } = [];

    public Task OnConnectedAsync(string connectionId) => Record($"connected:{connectionId}");

    public Task OnConnectionBoundAsync(string connectionId, string sessionId, string webBrowserId) => Record($"bound:{connectionId}");

    public Task OnDisconnectedAsync(string connectionId) => Record($"disconnected:{connectionId}");

    private Task Record(string value)
    {
        lock (Events)
        {
            Events.Add(value);
        }
        return Task.CompletedTask;
    }
}

internal sealed class RecordingConnectionChangeNotifier : IConnectionChangeNotifier
{
    public List<(string UserId, string SessionId)> Calls { get; } = [];

    public Task OnSessionConnectionChangedAsync(string userId, string sessionId)
    {
        lock (Calls)
        {
            Calls.Add((userId, sessionId));
        }
        return Task.CompletedTask;
    }
}

internal sealed class RecordingSignOutObserver : IChannelSignOutObserver
{
    public List<BrowserSignedOutContext> Contexts { get; } = [];

    public Task OnBrowserSignedOutAsync(BrowserSignedOutContext context)
    {
        Assert.IsAssignableFrom<HttpRequest>(context.HttpRequest);
        Contexts.Add(context);
        return Task.CompletedTask;
    }
}
