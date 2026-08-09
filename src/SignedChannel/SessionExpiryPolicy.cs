using Microsoft.Extensions.Options;

namespace SignedChannel;

/// <summary>
/// Idle- and absolute-expiry knobs. Expressed in seconds rather than TimeSpans so a test harness
/// can shrink the window from configuration alone.
/// </summary>
public sealed class SessionExpiryOptions
{
    /// <summary>Inactivity a signed-in session survives. Default 30 minutes.</summary>
    public int IdleSeconds { get; set; } = 1800;

    /// <summary>How long before expiry the client is expected to warn. Default 2 minutes.</summary>
    public int WarningSeconds { get; set; } = 120;

    /// <summary>
    /// Ceiling on total session lifetime regardless of activity. Default 8 hours. Worth aligning
    /// with any authentication cookie the application issues alongside the session, so the two
    /// cannot outlive each other.
    /// </summary>
    public int AbsoluteSeconds { get; set; } = 28800;

    /// <summary>
    /// Minimum gap between activity writes. Without it every request writes to the store purely to
    /// record that a request happened. Default 30 seconds.
    /// </summary>
    public int TouchThrottleSeconds { get; set; } = 30;
}

/// <summary>
/// When a signed-in session expires. The single source of truth for it: the dispatcher enforces it,
/// stamps the result onto every response so the client can count down against the same instant, and
/// anything else that needs to agree (an authentication cookie validator, a keep-alive action)
/// should ask here rather than recompute.
/// </summary>
public sealed class SessionExpiryPolicy
{
    private readonly SessionExpiryOptions _options;

    /// <summary>Creates the policy from configured options.</summary>
    public SessionExpiryPolicy(IOptions<SessionExpiryOptions> options) => _options = options.Value;

    /// <summary>How long before expiry the client is expected to warn.</summary>
    public int WarningSeconds => _options.WarningSeconds;

    /// <summary>Minimum gap between activity writes.</summary>
    public int TouchThrottleSeconds => _options.TouchThrottleSeconds;

    /// <summary>
    /// The instant the session expires: the earlier of the sliding idle window and the absolute
    /// cap. Taking the earlier of the two is what stops activity from extending a session forever.
    /// </summary>
    public DateTimeOffset ComputeExpiry(ChannelSessionRecord session)
    {
        var lastActivity = session.LastActivityDateTimeOffset ?? session.StartDateTimeOffset;
        var idleExpiry = lastActivity.AddSeconds(_options.IdleSeconds);
        var absoluteExpiry = session.StartDateTimeOffset.AddSeconds(_options.AbsoluteSeconds);
        return idleExpiry < absoluteExpiry ? idleExpiry : absoluteExpiry;
    }

    /// <summary>Whether the session is past its expiry at the given instant.</summary>
    public bool IsExpired(ChannelSessionRecord session, DateTimeOffset now) =>
        now >= ComputeExpiry(session);
}
