using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using MongoDbService;

namespace SignedChannel.MongoDb;

/// <summary>
/// MongoDB-backed instruction storage, shared by every instance — the browser's download may land
/// on a different instance from the push. Documents go with a TTL index, so an instruction nobody
/// collected does not linger.
/// </summary>
public sealed class MongoChannelInstructionStore : IChannelInstructionStore
{
    /// <summary>Collection instructions are stored in unless another name is given.</summary>
    public const string DefaultCollectionName = "channelInstructions";

    private sealed class InstructionDocument
    {
        [BsonId]
        public required string Id { get; init; }

        public required string SessionId { get; init; }

        public required string Name { get; init; }

        public string? Payload { get; init; }

        public required string TokenHash { get; init; }

        /// <summary>UTC; the TTL index removes the document shortly after.</summary>
        public required DateTime ExpireAt { get; init; }
    }

    private readonly IMongoCollection<InstructionDocument> _instructions;

    /// <summary>Creates the store over the configured Mongo connection.</summary>
    public MongoChannelInstructionStore(MongoService mongoService, string collectionName = DefaultCollectionName)
    {
        ArgumentNullException.ThrowIfNull(mongoService);
        _instructions = mongoService.Database.GetCollection<InstructionDocument>(collectionName);
        _instructions.Indexes.CreateOne(new CreateIndexModel<InstructionDocument>(
            Builders<InstructionDocument>.IndexKeys.Ascending(document => document.ExpireAt),
            new CreateIndexOptions { ExpireAfter = TimeSpan.Zero }));
    }

    /// <inheritdoc />
    public async Task<string> StoreAsync(string sessionId, ChannelInstruction instruction, TimeSpan validity)
    {
        ArgumentNullException.ThrowIfNull(instruction);

        var (messageId, token) = ChannelInstructionPointer.Create();
        await _instructions.InsertOneAsync(new InstructionDocument
        {
            Id = messageId,
            SessionId = sessionId,
            Name = instruction.Name,
            Payload = instruction.Payload,
            TokenHash = ChannelInstructionPointer.HashToken(token),
            ExpireAt = DateTime.UtcNow.Add(validity)
        });
        return ChannelInstructionPointer.Format(messageId, token);
    }

    /// <inheritdoc />
    public async Task<ChannelInstruction?> DownloadAsync(string sessionId, string pointer)
    {
        if (!ChannelInstructionPointer.TryParse(pointer, out var messageId, out var token))
        {
            return null;
        }

        // Deleted in the same round trip it is read, so two downloads cannot both succeed. The
        // session is part of the filter: another session's attempt leaves the instruction in place.
        var document = await _instructions.FindOneAndDeleteAsync(d => d.Id == messageId && d.SessionId == sessionId);
        if (document is null || document.ExpireAt < DateTime.UtcNow ||
            document.TokenHash != ChannelInstructionPointer.HashToken(token))
        {
            return null;
        }

        return new ChannelInstruction(document.Name, document.Payload);
    }
}
