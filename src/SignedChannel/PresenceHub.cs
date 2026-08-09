using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SignedChannel;

/// <summary>
/// The set of SignalR connections that are live right now, in this process.
/// <para>
/// The session store also records connection ids, but those go stale two ways that no amount of
/// care at the call site can fix: a registration can land after its socket has already gone (fast
/// navigation, where the disconnect's removal runs before the addition), and a process restart
/// orphans every recorded id, because no disconnect event will ever arrive for them. Liveness is
/// therefore something to check here and heal in the store, not something the store can be trusted
/// for on its own.
/// </para>
/// </summary>
public sealed class ConnectionRegistry
{
    private readonly ConcurrentDictionary<string, byte> _live = new();

    /// <summary>Records a connection as live.</summary>
    public void Add(string connectionId) => _live.TryAdd(connectionId, 0);

    /// <summary>Records a connection as gone.</summary>
    public void Remove(string connectionId) => _live.TryRemove(connectionId, out _);

    /// <summary>Whether this connection is live in this process.</summary>
    public bool IsLive(string connectionId) => _live.ContainsKey(connectionId);
}

/// <summary>
/// The channel's SignalR hub. Server-to-client push only: the browser never invokes anything here,
/// it posts signed requests over HTTP, so nothing reaches the application without passing the
/// signature check.
/// <para>
/// The connection lifecycle is the substance of this class. A closed tab has to be reflected
/// immediately, because anything showing "open in N places" is only truthful if disconnects are
/// recorded as they happen.
/// </para>
/// </summary>
public class PresenceHub : Hub
{
    private readonly IChannelSessionStore _sessions;
    private readonly ConnectionRegistry _registry;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<PresenceHub> _logger;

    /// <summary>Creates the hub.</summary>
    public PresenceHub(
        IChannelSessionStore sessions,
        ConnectionRegistry registry,
        IServiceProvider serviceProvider,
        ILogger<PresenceHub> logger)
    {
        _sessions = sessions;
        _registry = registry;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    /// <summary>
    /// Accepted for clients that call it on connect. The hub carries no browser-to-server
    /// semantics, so there is deliberately nothing to do.
    /// </summary>
    public Task JoinPage(string page) => Task.CompletedTask;

    /// <inheritdoc />
    public override async Task OnConnectedAsync()
    {
        _registry.Add(Context.ConnectionId);

        var observer = _serviceProvider.GetService<IPresenceObserver>();
        if (observer is not null)
        {
            await observer.OnConnectedAsync(Context.ConnectionId);
        }

        // When a live tab count disagrees with what someone can see on screen, this pair of log
        // lines is what settles which of the two is wrong.
        _logger.LogInformation("Presence connect {ConnectionId}", Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    /// <inheritdoc />
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _registry.Remove(Context.ConnectionId);

        var observer = _serviceProvider.GetService<IPresenceObserver>();
        if (observer is not null)
        {
            await observer.OnDisconnectedAsync(Context.ConnectionId);
        }

        var session = await _sessions.RemoveConnectionIdAsync(Context.ConnectionId);

        _logger.LogInformation("Presence disconnect {ConnectionId} (session {SessionId}){Reason}",
            Context.ConnectionId, session?.Id ?? "none",
            exception is null ? string.Empty : $" after {exception.GetType().Name}");

        if (!string.IsNullOrEmpty(session?.UserId))
        {
            var notifier = _serviceProvider.GetService<IConnectionChangeNotifier>();
            if (notifier is not null)
            {
                // Detached: a notification channel being slow or down must not hold up a
                // disconnect, and there is nothing here that could act on a failure anyway.
                _ = notifier.OnSessionConnectionChangedAsync(session.UserId, session.Id);
            }
        }

        await base.OnDisconnectedAsync(exception);
    }
}

/// <summary>
/// Notified as raw connections come and go, before any session is known.
/// <para>
/// Optional, and distinct from <see cref="IConnectionChangeNotifier"/>: this fires for every
/// connection including anonymous ones, which is what makes it the right place for a durable
/// connection record — the registry cannot survive a restart, a stored row can.
/// </para>
/// </summary>
public interface IPresenceObserver
{
    /// <summary>A connection opened.</summary>
    Task OnConnectedAsync(string connectionId);

    /// <summary>A connection closed.</summary>
    Task OnDisconnectedAsync(string connectionId);
}
