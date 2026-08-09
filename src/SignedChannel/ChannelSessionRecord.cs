namespace SignedChannel;

/// <summary>
/// One browser session: the pair of public keys the browser generated, the hash of its cookie
/// secret, and the connections it currently holds.
/// <para>
/// The type carries no persistence attributes, so the choice of store stays outside this package.
/// It is deliberately left open for inheritance: an application that wants extra per-session state
/// can derive from it and keep everything in one document, while the channel continues to work
/// against this view of it.
/// </para>
/// </summary>
public class ChannelSessionRecord
{
    /// <summary>
    /// The session id: a deterministic hash of the two public keys
    /// (<see cref="ChannelCrypto.ComputeSessionId"/>). It is derived from key material only —
    /// never from anything identifying the person — so it reveals nothing about who holds it.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>Identifies the browser across sessions; supplied and persisted by the client.</summary>
    public required string WebBrowserId { get; init; }

    /// <summary>
    /// SHA-512 of the session cookie's secret. The secret itself is never stored, so a dump of
    /// this collection does not let anyone forge the cookie.
    /// </summary>
    public required string CookieIdHash { get; init; }

    /// <summary>When the session was registered; the start of the absolute-expiry window.</summary>
    public required DateTimeOffset StartDateTimeOffset { get; init; }

    /// <summary>The public key every signed request from this session is verified against.</summary>
    public required string VerifyingPublicKeyBase64 { get; init; }

    /// <summary>The public key used to encrypt the session id back to the browser.</summary>
    public required string EncryptionPublicKeyBase64 { get; init; }

    /// <summary>The language the browser asked for at registration.</summary>
    public required string PreferredLanguageIsoCode { get; init; }

    /// <summary>Channel version this session registered with.</summary>
    public required string ApiVersion { get; init; }

    /// <summary>Live SignalR connections (tabs) belonging to this session.</summary>
    public List<string> ConnectionIds { get; set; } = [];

    /// <summary>
    /// Set once the application authenticates the session. Opaque to the channel: it is echoed to
    /// actions and used to decide whether a request counts as signed in, never interpreted.
    /// </summary>
    public string? UserId { get; set; }

    /// <summary>
    /// When set, the session is logged out and is treated as anonymous again — otherwise a
    /// remotely-logged-out browser could keep acting signed in.
    /// </summary>
    public DateTimeOffset? LogoutDateTimeOffset { get; set; }

    /// <summary>
    /// Last activity over the channel, which slides the idle-expiry window. Absent on records
    /// written before expiry tracking existed, in which case <see cref="StartDateTimeOffset"/>
    /// stands in.
    /// </summary>
    public DateTimeOffset? LastActivityDateTimeOffset { get; set; }
}
