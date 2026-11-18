using System.Net;
using Microsoft.Extensions.DependencyInjection;
using SignedChannel.Tests.Host.Jobs.Actions.WebApp.Start;

namespace SignedChannel.Tests;

/// <summary>
/// The two optional dispatcher hooks: the job registry (cancellable work that outlives its request)
/// and the rate limiter. Leaving either unregistered must keep the behaviour hosts already rely on.
/// </summary>
[Collection(nameof(JobAndRateLimitTests))] // StartAction.LastToken is static: don't run these in parallel
public sealed class JobAndRateLimitTests
{
    [Fact]
    public async Task Without_a_job_registry_the_response_has_no_job_id()
    {
        await using var host = await TestChannelHost.StartAsync();
        var tab = await host.OpenSessionAsync();

        var (status, body) = await tab.SendAsync("Jobs.Start", new { percentageComplete = 40 });

        Assert.Equal(202, status);
        Assert.True(!body.TryGetProperty("jobId", out var jobId) || jobId.ValueKind == System.Text.Json.JsonValueKind.Null);
    }

    [Fact]
    public async Task A_complete_action_is_registered_stamped_and_released()
    {
        var registry = new RecordingJobRegistry();
        await using var host = await TestChannelHost.StartAsync(s => s.AddSingleton<IChannelJobRegistry>(registry));
        var tab = await host.OpenSessionAsync();

        var (status, body) = await tab.SendAsync("Jobs.Start", new { percentageComplete = 100 });

        Assert.Equal(202, status);
        var registered = Assert.Single(registry.Registered);
        Assert.Equal("Jobs.Start", registered.Context.ActionName);
        Assert.Equal(tab.SessionId, registered.Context.SessionId);
        Assert.Equal(registered.Context.JobId, body.GetProperty("jobId").GetString());
        Assert.Equal([registered.Context.JobId], registry.Unregistered);
        Assert.Empty(registry.StillRunning);
        Assert.Equal(registered.Source.Token, StartAction.LastToken); // the registry's token, not the request's
    }

    [Fact]
    public async Task A_job_still_running_is_announced_kept_and_cancellable_after_the_response()
    {
        var registry = new RecordingJobRegistry();
        await using var host = await TestChannelHost.StartAsync(s => s.AddSingleton<IChannelJobRegistry>(registry));
        var tab = await host.OpenSessionAsync();

        var (status, body) = await tab.SendAsync("Jobs.Start", new { percentageComplete = 40 });

        Assert.Equal(202, status);
        var registered = Assert.Single(registry.Registered);
        Assert.Equal(40, body.GetProperty("percentageComplete").GetInt32());
        Assert.Equal(registered.Context.JobId, Assert.Single(registry.StillRunning).JobId);
        Assert.Empty(registry.Unregistered); // the action owns it now

        // The request is over, and the token the action holds still answers to the registry.
        var token = StartAction.LastToken;
        Assert.False(token.IsCancellationRequested);
        await registered.Source.CancelAsync();
        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public async Task A_failing_action_releases_its_job()
    {
        var registry = new RecordingJobRegistry();
        await using var host = await TestChannelHost.StartAsync(s => s.AddSingleton<IChannelJobRegistry>(registry));
        var tab = await host.OpenSessionAsync();

        var (status, _) = await tab.SendAsync("Jobs.Start", new { fail = true });

        Assert.Equal(500, status);
        Assert.Equal([Assert.Single(registry.Registered).Context.JobId], registry.Unregistered);
        Assert.Empty(registry.StillRunning);
    }

    [Fact]
    public async Task A_refused_action_is_429_and_does_not_run()
    {
        var limiter = new RecordingRateLimiter { RefuseScope = ChannelRateLimitScope.Action };
        var registry = new RecordingJobRegistry();
        await using var host = await TestChannelHost.StartAsync(s =>
        {
            s.AddSingleton<IChannelRateLimiter>(limiter);
            s.AddSingleton<IChannelJobRegistry>(registry);
        });
        var tab = await host.OpenSessionAsync();

        var (status, _) = await tab.SendAsync("Jobs.Start");

        Assert.Equal(429, status);
        Assert.Empty(registry.Registered); // never reached the action
        var asked = limiter.Asked.Single(c => c.Scope == ChannelRateLimitScope.Action);
        Assert.Equal($"session:{tab.SessionId}", asked.Key);
        Assert.Equal("Jobs.Start", asked.ActionName);
    }

    [Fact]
    public async Task A_refused_registration_is_429_and_keyed_by_browser()
    {
        var limiter = new RecordingRateLimiter { RefuseScope = ChannelRateLimitScope.Registration };
        await using var host = await TestChannelHost.StartAsync(s => s.AddSingleton<IChannelRateLimiter>(limiter));

        var refused = await Assert.ThrowsAsync<HttpRequestException>(host.OpenSessionAsync);

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Empty(host.Sessions.Sessions);
        var asked = Assert.Single(limiter.Asked);
        Assert.Equal("browser:browser-1", asked.Key);
        Assert.Null(asked.ActionName);
    }

    [Fact]
    public async Task An_allowing_limiter_is_asked_for_every_request()
    {
        var limiter = new RecordingRateLimiter();
        await using var host = await TestChannelHost.StartAsync(s => s.AddSingleton<IChannelRateLimiter>(limiter));
        var tab = await host.OpenSessionAsync();

        var (status, _) = await tab.SendAsync("Jobs.Start");

        Assert.Equal(202, status);
        Assert.Equal([ChannelRateLimitScope.Registration, ChannelRateLimitScope.Action], limiter.Asked.Select(c => c.Scope));
    }
}

internal sealed class RecordingJobRegistry : IChannelJobRegistry
{
    public List<(ChannelJobContext Context, CancellationTokenSource Source)> Registered { get; } = [];

    public List<string> Unregistered { get; } = [];

    public List<ChannelJobContext> StillRunning { get; } = [];

    public CancellationToken Register(ChannelJobContext context)
    {
        var source = new CancellationTokenSource();
        lock (Registered)
        {
            Registered.Add((context, source));
        }
        return source.Token;
    }

    public void Unregister(string jobId)
    {
        lock (Unregistered)
        {
            Unregistered.Add(jobId);
        }
    }

    public Task OnStillRunningAsync(ChannelJobContext context, MessageWebBrowserResponseBase response)
    {
        lock (StillRunning)
        {
            StillRunning.Add(context);
        }
        return Task.CompletedTask;
    }
}

internal sealed class RecordingRateLimiter : IChannelRateLimiter
{
    /// <summary>Refuse every request of this scope; null allows everything.</summary>
    public ChannelRateLimitScope? RefuseScope { get; init; }

    public List<ChannelRateLimitContext> Asked { get; } = [];

    public Task<bool> IsAllowedAsync(ChannelRateLimitContext context)
    {
        lock (Asked)
        {
            Asked.Add(context);
        }
        return Task.FromResult(context.Scope != RefuseScope);
    }
}
