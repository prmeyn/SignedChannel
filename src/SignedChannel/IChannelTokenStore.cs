using System.Collections.Concurrent;
using System.Security.Cryptography;
using Meyn.Utilities;

namespace SignedChannel;

/// <summary>
/// Single-use secrets bound to an id. The channel uses it for the connection challenge: the token
/// is pushed, encrypted, to one connection, and only a browser holding the session's private key
/// can read it back to prove that connection is its own.
/// </summary>
public interface IChannelTokenStore
{
    /// <summary>Generates a token for the id, replacing any earlier one, and returns it.</summary>
    Task<string> GenerateAsync(string id, TimeSpan validity);

    /// <summary>
    /// Consumes the id's token: true only if it matched and was unexpired. The token is gone
    /// afterwards either way, so a wrong guess burns it.
    /// </summary>
    Task<bool> ConsumeAndValidateAsync(string id, string token);
}

/// <summary>
/// Process-local token storage: the default, and right for a single instance or tests. With more
/// than one instance use a shared store (<c>SignedChannel.MongoDb</c>).
/// </summary>
public sealed class InMemoryChannelTokenStore : IChannelTokenStore
{
    private sealed record Entry(string TokenHash, DateTimeOffset ExpiresAt);

    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    /// <inheritdoc />
    public Task<string> GenerateAsync(string id, TimeSpan validity)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (key, entry) in _entries)
        {
            if (entry.ExpiresAt <= now)
            {
                _entries.TryRemove(key, out _);
            }
        }

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        _entries[id] = new Entry(CryptoUtils.ComputeSha512Hash(token), now.Add(validity));
        return Task.FromResult(token);
    }

    /// <inheritdoc />
    public Task<bool> ConsumeAndValidateAsync(string id, string token) =>
        Task.FromResult(
            _entries.TryRemove(id, out var entry) &&
            entry.ExpiresAt > DateTimeOffset.UtcNow &&
            entry.TokenHash == CryptoUtils.ComputeSha512Hash(token));
}
