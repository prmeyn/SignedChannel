using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson.Serialization;

namespace SignedChannel.MongoDb;

/// <summary>
/// Registration for MongoDB-backed session storage.
/// </summary>
public static class SignedChannelMongoDbExtensions
{
    private static readonly Lock ClassMapLock = new();
    private static bool _classMapRegistered;

    /// <summary>
    /// Registers <see cref="MongoChannelSessionStore"/> as the channel's
    /// <see cref="IChannelSessionStore"/>.
    /// <para>
    /// Expects a <c>MongoService</c> to be registered already — this package uses the
    /// application's existing connection rather than opening one of its own.
    /// </para>
    /// <para>
    /// An application storing extra per-session state should register its own derived store
    /// instead of calling this, and call <see cref="RegisterSessionClassMap"/> to pick up the same
    /// serialization settings.
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
