namespace SignedChannel;

/// <summary>
/// The channel's own wire names. Everything under <see cref="ReservedPrefix"/> belongs to the
/// package: the dispatcher resolves these to built-in actions, and a host action that would land
/// under the same prefix fails startup rather than silently shadowing (or being shadowed by) one.
/// <para>
/// These are the protocol. The browser client hard-codes the same strings, which is why both halves
/// version in lockstep.
/// </para>
/// </summary>
public static class ChannelNames
{
    /// <summary>Wire-name prefix reserved for the channel itself.</summary>
    public const string ReservedPrefix = "Channel.";

    // --- browser → server requests ------------------------------------------------------------

    /// <summary>First request on a new connection: binds it to the session and starts the challenge.</summary>
    public const string Register = "Channel.Register";

    /// <summary>Answers the connection challenge; on success the connection joins its session's group.</summary>
    public const string VerifyConnection = "Channel.VerifyConnection";

    /// <summary>Exchanges an instruction pointer for the instruction itself.</summary>
    public const string DownloadInstruction = "Channel.DownloadInstruction";

    /// <summary>"Keep me signed in": reads back the freshly slid expiry.</summary>
    public const string KeepAlive = "Channel.KeepAlive";

    /// <summary>Signs this session out, and every other tab of it.</summary>
    public const string SignOut = "Channel.SignOut";

    // --- server → browser pushes --------------------------------------------------------------

    /// <summary>The connection challenge, encrypted to the session key and addressed to one connection.</summary>
    public const string ConnectionChallengePush = "Channel.ConnectionChallenge";

    /// <summary>A connection joined its session's group.</summary>
    public const string ConnectionVerifiedPush = "Channel.ConnectionVerified";

    /// <summary>An instruction is waiting; the encrypted payload is the pointer to download it with.</summary>
    public const string InstructionPush = "Channel.Instruction";

    /// <summary>The session was signed in.</summary>
    public const string SignedInPush = "Channel.SignedIn";

    /// <summary>The session was signed out — remotely, from another tab, or by expiry handling.</summary>
    public const string SignedOutPush = "Channel.SignedOut";

    // --- 401 reason codes ({"code": …} in the body) ------------------------------------------

    /// <summary>The signed-in session ran past its idle or absolute window; it is now ended.</summary>
    public const string SessionExpiredCode = "session_expired";

    /// <summary>
    /// The session was signed out (here, in another tab, remotely, or by expiry) and is ended for
    /// good. The browser's answer is to drop its keys and start a new session — never to sign the
    /// old one in again.
    /// </summary>
    public const string SessionEndedCode = "session_ended";

    /// <summary>The id the connection challenge's single-use token is stored under.</summary>
    internal static string ConnectionChallengeTokenId(string sessionId, string connectionId) =>
        $"conn:{sessionId}:{connectionId}";
}
