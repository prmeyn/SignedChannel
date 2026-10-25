using System.Collections.Concurrent;
using System.Security.Cryptography;
using Meyn.Utilities;

namespace SignedChannel;

/// <summary>
/// An instruction for a session's browser: a host-defined name and an optional payload.
/// <para>
/// The name is the application's own event — the channel neither knows nor checks the set.
/// The payload is usually JSON, but it is carried as an opaque string.
/// </para>
/// </summary>
/// <param name="Name">What the browser should do, as the host defines it.</param>
/// <param name="Payload">Data for it, or null when there is none.</param>
public sealed record ChannelInstruction(string Name, string? Payload);

/// <summary>
/// Holds instructions between the push that announces them and the download that collects them.
/// <para>
/// This indirection is how instructions get round RSA's size limit: only a short pointer is
/// encrypted and pushed, and the instruction itself comes back over the signed channel. The pointer
/// carries a random token whose hash is all the store keeps, so a dump of the store cannot be
/// turned into downloads.
/// </para>
/// <para>
/// One pointer goes to every tab of the session, so a download must not consume the instruction —
/// that would hand it to whichever tab asked first and nothing to the others. It stays collectable
/// by its own session until it expires, and only by that session: the download is a signed request,
/// so nobody else can present the pointer as that session.
/// </para>
/// </summary>
public interface IChannelInstructionStore
{
    /// <summary>Stores an instruction for a session and returns the pointer to push.</summary>
    Task<string> StoreAsync(string sessionId, ChannelInstruction instruction, TimeSpan validity);

    /// <summary>
    /// The instruction the pointer names, if it belongs to this session, is unexpired, and the
    /// pointer's token matches; null otherwise. Does not remove it — expiry does.
    /// </summary>
    Task<ChannelInstruction?> DownloadAsync(string sessionId, string pointer);
}

/// <summary>
/// Pointer format shared by the stores: <c>{messageId}_{token}</c>, with the token's SHA-512 kept
/// for comparison.
/// </summary>
public static class ChannelInstructionPointer
{
    /// <summary>A new message id and one-time token.</summary>
    public static (string MessageId, string Token) Create() =>
        (Guid.NewGuid().ToString("N"), Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));

    /// <summary>Formats a pointer.</summary>
    public static string Format(string messageId, string token) => $"{messageId}_{token}";

    /// <summary>Splits a pointer; false if it is malformed.</summary>
    public static bool TryParse(string? pointer, out string messageId, out string token)
    {
        messageId = token = string.Empty;
        var parts = pointer?.Split('_', 2);
        if (parts is not { Length: 2 } || parts[0].Length == 0 || parts[1].Length == 0)
        {
            return false;
        }

        messageId = parts[0];
        token = parts[1];
        return true;
    }

    /// <summary>The hash a store keeps in place of the token.</summary>
    public static string HashToken(string token) => CryptoUtils.ComputeSha512Hash(token);
}

/// <summary>
/// Process-local instruction storage: the default, and right for a single instance or tests. A
/// deployment with more than one instance needs a shared store (<c>SignedChannel.MongoDb</c>), since
/// the download may land on a different instance from the push.
/// </summary>
public sealed class InMemoryChannelInstructionStore : IChannelInstructionStore
{
    private sealed record Entry(string SessionId, ChannelInstruction Instruction, string TokenHash, DateTimeOffset ExpiresAt);

    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    /// <inheritdoc />
    public Task<string> StoreAsync(string sessionId, ChannelInstruction instruction, TimeSpan validity)
    {
        PruneExpired();
        var (messageId, token) = ChannelInstructionPointer.Create();
        _entries[messageId] = new Entry(sessionId, instruction, ChannelInstructionPointer.HashToken(token), DateTimeOffset.UtcNow.Add(validity));
        return Task.FromResult(ChannelInstructionPointer.Format(messageId, token));
    }

    /// <inheritdoc />
    public Task<ChannelInstruction?> DownloadAsync(string sessionId, string pointer)
    {
        if (!ChannelInstructionPointer.TryParse(pointer, out var messageId, out var token))
        {
            return Task.FromResult<ChannelInstruction?>(null);
        }

        var valid = _entries.TryGetValue(messageId, out var entry) &&
            entry.SessionId == sessionId &&
            entry.ExpiresAt > DateTimeOffset.UtcNow &&
            entry.TokenHash == ChannelInstructionPointer.HashToken(token);
        return Task.FromResult(valid ? entry!.Instruction : null);
    }

    private void PruneExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (key, entry) in _entries)
        {
            if (entry.ExpiresAt <= now)
            {
                _entries.TryRemove(key, out _);
            }
        }
    }
}
