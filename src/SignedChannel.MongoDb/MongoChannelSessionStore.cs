using MongoDB.Driver;
using MongoDbService;

namespace SignedChannel.MongoDb;

/// <summary>
/// MongoDB-backed session storage.
/// <para>
/// Generic in the record type, and every member is virtual, so an application that needs extra
/// per-session state can derive a record from <see cref="ChannelSessionRecord"/>, derive a store
/// from this, and keep everything in one document — rather than maintaining a parallel collection
/// keyed by session id and keeping the two in step.
/// </para>
/// </summary>
/// <typeparam name="TRecord">
/// The stored record. Use <see cref="ChannelSessionRecord"/> unless the application has its own.
/// </typeparam>
public class MongoChannelSessionStore<TRecord> : IChannelSessionStore
    where TRecord : ChannelSessionRecord
{
    /// <summary>The underlying collection, for queries a derived store adds.</summary>
    protected IMongoCollection<TRecord> Sessions { get; }

    /// <summary>
    /// The same collection seen as the base record.
    /// <para>
    /// Registration happens inside the channel, which knows nothing of any derived record, so the
    /// record it hands to <see cref="CreateAsync"/> is always a base instance and cannot be
    /// inserted through a collection typed to a derived one. Writing it through this handle is
    /// well-defined instead: the derived fields are simply absent from the new document, which is
    /// exactly right — they describe state the session has not reached yet — and they read back as
    /// their defaults.
    /// </para>
    /// </summary>
    private readonly IMongoCollection<ChannelSessionRecord> _sessionsAsBase;

    /// <summary>Creates the store over the configured Mongo connection.</summary>
    /// <param name="mongoService">The application's Mongo connection.</param>
    /// <param name="collectionName">Collection to store sessions in.</param>
    public MongoChannelSessionStore(MongoService mongoService, string collectionName = DefaultCollectionName)
    {
        ArgumentNullException.ThrowIfNull(mongoService);
        Sessions = mongoService.Database.GetCollection<TRecord>(collectionName);
        _sessionsAsBase = mongoService.Database.GetCollection<ChannelSessionRecord>(collectionName);
    }

    /// <summary>Collection sessions are stored in unless another name is given.</summary>
    public const string DefaultCollectionName = "webBrowserSessions";

    /// <inheritdoc />
    public virtual async Task<ChannelSessionRecord?> GetByIdAsync(string sessionId) =>
        await Sessions.Find(session => session.Id == sessionId).FirstOrDefaultAsync();

    /// <inheritdoc />
    public virtual Task CreateAsync(ChannelSessionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        // Written through the base-typed handle rather than cast: see _sessionsAsBase. A derived
        // instance still serializes as its own type when one is passed.
        return record is TRecord typedRecord
            ? Sessions.InsertOneAsync(typedRecord)
            : _sessionsAsBase.InsertOneAsync(record);
    }

    /// <inheritdoc />
    public virtual Task AddConnectionIdAsync(string sessionId, string connectionId)
    {
        var update = Builders<TRecord>.Update.AddToSet(session => session.ConnectionIds, connectionId);
        return Sessions.UpdateOneAsync(session => session.Id == sessionId, update);
    }

    /// <inheritdoc />
    public virtual async Task<ChannelSessionRecord?> RemoveConnectionIdAsync(string connectionId)
    {
        // One round trip, returning the pre-update image: the caller needs the user id that was on
        // the session in order to react to the disconnect, and the update does not change it.
        return await Sessions.FindOneAndUpdateAsync<TRecord?>(
            Builders<TRecord>.Filter.AnyEq(session => session.ConnectionIds, connectionId),
            Builders<TRecord>.Update.Pull(session => session.ConnectionIds, connectionId),
            new FindOneAndUpdateOptions<TRecord, TRecord?>
            {
                ReturnDocument = ReturnDocument.Before
            });
    }

    /// <inheritdoc />
    public virtual Task PruneConnectionIdsAsync(string sessionId, IReadOnlyCollection<string> deadConnectionIds)
    {
        var update = Builders<TRecord>.Update.PullAll(session => session.ConnectionIds, deadConnectionIds);
        return Sessions.UpdateOneAsync(session => session.Id == sessionId, update);
    }

    /// <inheritdoc />
    public virtual Task SetUserIdAsync(string sessionId, string userId)
    {
        // Signing in supersedes any earlier logout, so the tombstone is cleared here. Leaving it
        // set is subtly fatal: a client that reuses its session id would authenticate successfully
        // and still be treated as logged out on the very next request, looping back to sign-in
        // forever. Also starts the idle clock.
        var update = Builders<TRecord>.Update
            .Set(session => session.UserId, userId)
            .Set(session => session.LogoutDateTimeOffset, (DateTimeOffset?)null)
            .Set(session => session.LastActivityDateTimeOffset, DateTimeOffset.UtcNow);
        return Sessions.UpdateOneAsync(session => session.Id == sessionId, update);
    }

    /// <inheritdoc />
    public virtual Task SetLogoutAsync(string sessionId)
    {
        var update = Builders<TRecord>.Update.Set(session => session.LogoutDateTimeOffset, DateTimeOffset.UtcNow);
        return Sessions.UpdateOneAsync(session => session.Id == sessionId, update);
    }

    /// <inheritdoc />
    public virtual Task TouchActivityAsync(string sessionId, DateTimeOffset now)
    {
        var update = Builders<TRecord>.Update.Set(session => session.LastActivityDateTimeOffset, now);
        return Sessions.UpdateOneAsync(session => session.Id == sessionId, update);
    }
}

/// <summary>
/// MongoDB session storage for applications that need no extra per-session state.
/// </summary>
public class MongoChannelSessionStore : MongoChannelSessionStore<ChannelSessionRecord>
{
    /// <summary>Creates the store over the configured Mongo connection.</summary>
    public MongoChannelSessionStore(MongoService mongoService, string collectionName = DefaultCollectionName)
        : base(mongoService, collectionName)
    {
    }
}
