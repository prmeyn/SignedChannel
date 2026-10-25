using System.Security.Cryptography;
using Meyn.Utilities;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using MongoDbService;

namespace SignedChannel.MongoDb;

/// <summary>
/// MongoDB-backed single-use tokens, shared by every instance. Only the token's hash is stored,
/// and a TTL index removes expired ones.
/// </summary>
public sealed class MongoChannelTokenStore : IChannelTokenStore
{
    /// <summary>Collection tokens are stored in unless another name is given.</summary>
    public const string DefaultCollectionName = "channelTokens";

    private sealed class TokenDocument
    {
        [BsonId]
        public required string Id { get; init; }

        public required string TokenHash { get; init; }

        /// <summary>UTC; the TTL index removes the document shortly after.</summary>
        public required DateTime ExpireAt { get; init; }
    }

    private readonly IMongoCollection<TokenDocument> _tokens;

    /// <summary>Creates the store over the configured Mongo connection.</summary>
    public MongoChannelTokenStore(MongoService mongoService, string collectionName = DefaultCollectionName)
    {
        ArgumentNullException.ThrowIfNull(mongoService);
        _tokens = mongoService.Database.GetCollection<TokenDocument>(collectionName);
        _tokens.Indexes.CreateOne(new CreateIndexModel<TokenDocument>(
            Builders<TokenDocument>.IndexKeys.Ascending(document => document.ExpireAt),
            new CreateIndexOptions { ExpireAfter = TimeSpan.Zero }));
    }

    /// <inheritdoc />
    public async Task<string> GenerateAsync(string id, TimeSpan validity)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var document = new TokenDocument
        {
            Id = id,
            TokenHash = CryptoUtils.ComputeSha512Hash(token),
            ExpireAt = DateTime.UtcNow.Add(validity)
        };

        // Upsert: a fresh challenge for the same id replaces the old one.
        await _tokens.ReplaceOneAsync(d => d.Id == id, document, new ReplaceOptions { IsUpsert = true });
        return token;
    }

    /// <inheritdoc />
    public async Task<bool> ConsumeAndValidateAsync(string id, string token)
    {
        // Removed whether or not it matches, so a token can be tried at most once.
        var document = await _tokens.FindOneAndDeleteAsync(d => d.Id == id);
        return document is not null &&
            document.ExpireAt >= DateTime.UtcNow &&
            document.TokenHash == CryptoUtils.ComputeSha512Hash(token);
    }
}
