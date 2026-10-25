using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson.Serialization;

namespace SignedChannel.MongoDb;

/// <summary>
/// Registration for MongoDB-backed channel storage.
/// </summary>
public static class SignedChannelMongoDbExtensions
{
    private static readonly Lock ClassMapLock = new();
    private static bool _classMapRegistered;

    /// <summary>
    /// Registers every store the channel needs on MongoDB: <see cref="MongoChannelSessionStore"/>
    /// as the <see cref="IChannelSessionStore"/>, plus the instruction and token stores
    /// (<see cref="AddSignedChannelMongoDbEphemeralStores"/>).
    /// <para>
    /// Expects a <c>MongoService</c> to be registered already — this package uses the
    /// application's existing connection rather than opening one of its own.
    /// </para>
    /// <para>
    /// An application storing extra per-session state should register its own derived session
    /// store instead of calling this, call <see cref="RegisterSessionClassMap"/> to pick up the same
    /// serialization settings, and call <see cref="AddSignedChannelMongoDbEphemeralStores"/> for the rest.
    /// </para>
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="collectionName">Collection to store sessions in.</param>
    public static IServiceCollection AddSignedChannelMongoDb(
        this IServiceCollection services,
        string collectionName = MongoChannelSessionStore.DefaultCollectionName)
    {
        RegisterSessionClassMap();

        services.AddSingleton<IChannelSessionStore>(serviceProvider =>
            new MongoChannelSessionStore(
                serviceProvider.GetRequiredService<MongoDbService.MongoService>(),
                collectionName));

        return services.AddSignedChannelMongoDbEphemeralStores();
    }

    /// <summary>
    /// Registers the short-lived stores — instructions awaiting download and connection-challenge
    /// tokens — on MongoDB, replacing the in-memory defaults whichever order the calls come in.
    /// Anything running more than one instance needs these shared, since a browser's follow-up
    /// request can land on a different instance from the push it answers.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="instructionCollectionName">Collection to store instructions in.</param>
    /// <param name="tokenCollectionName">Collection to store tokens in.</param>
    public static IServiceCollection AddSignedChannelMongoDbEphemeralStores(
        this IServiceCollection services,
        string instructionCollectionName = MongoChannelInstructionStore.DefaultCollectionName,
        string tokenCollectionName = MongoChannelTokenStore.DefaultCollectionName)
    {
        services.RemoveAll<IChannelInstructionStore>();
        services.AddSingleton<IChannelInstructionStore>(serviceProvider =>
            new MongoChannelInstructionStore(
                serviceProvider.GetRequiredService<MongoDbService.MongoService>(),
                instructionCollectionName));

        services.RemoveAll<IChannelTokenStore>();
        services.AddSingleton<IChannelTokenStore>(serviceProvider =>
            new MongoChannelTokenStore(
                serviceProvider.GetRequiredService<MongoDbService.MongoService>(),
                tokenCollectionName));

        return services;
    }

    /// <summary>
    /// Registers the serialization settings for <see cref="ChannelSessionRecord"/>. Safe to call
    /// more than once; only the first call has any effect.
    /// <para>
    /// The point of it is tolerating extra elements. An application that stores its own fields on
    /// the session document would otherwise get a deserialization failure the moment anything read
    /// that document back through the base type — a confusing way to discover that two views of
    /// one collection exist.
    /// </para>
    /// </summary>
    public static void RegisterSessionClassMap()
    {
        if (_classMapRegistered)
        {
            return;
        }

        lock (ClassMapLock)
        {
            if (_classMapRegistered)
            {
                return;
            }

            if (!BsonClassMap.IsClassMapRegistered(typeof(ChannelSessionRecord)))
            {
                BsonClassMap.RegisterClassMap<ChannelSessionRecord>(classMap =>
                {
                    classMap.AutoMap();
                    classMap.SetIgnoreExtraElements(true);
                });
            }

            _classMapRegistered = true;
        }
    }
}
