using Microsoft.Extensions.Options;

namespace SignedChannel;

/// <summary>
/// Bounds on how old a signed message may be.
/// </summary>
public sealed class FreshnessOptions
{
    /// <summary>
    /// How long a signed message stays acceptable. This is the replay window: a captured request
    /// can be replayed until it lapses, so shorter is safer, bounded below by how far client and
    /// server clocks realistically drift. Default 2 minutes.
    /// </summary>
    public int MaxAgeSeconds { get; set; } = 120;

    /// <summary>
    /// How far into the future a timestamp may sit before it is rejected, absorbing clock skew on
    /// the client. Default 1 minute.
    /// </summary>
    public int ClockSkewSeconds { get; set; } = 60;
}

/// <summary>
/// Whether a signed message is recent enough to act on.
/// <para>
/// This is not the security gate — the signature is — but it is what stops a valid captured
/// request from being replayable indefinitely.
/// </para>
/// </summary>
public sealed class TimestampFreshness
{
    private readonly FreshnessOptions _options;

    /// <summary>Creates the check from configured options.</summary>
    public TimestampFreshness(IOptions<FreshnessOptions> options) => _options = options.Value;

    /// <summary>
    /// Whether the value looks like a real timestamp at all, as opposed to a default-valued or
    /// obviously bogus one that would otherwise sail through the age comparison.
    /// </summary>
    public bool BeAValidDateWithOffset(DateTimeOffset timestamp) =>
        timestamp != default && timestamp.Year > 2000;

    /// <summary>Whether the timestamp falls inside the accepted window.</summary>
    public bool IsFresh(DateTimeOffset timestamp)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - timestamp > TimeSpan.FromSeconds(_options.MaxAgeSeconds))
        {
            return false;
        }
        if (timestamp - now > TimeSpan.FromSeconds(_options.ClockSkewSeconds))
        {
            return false;
        }
        return true;
    }
}
