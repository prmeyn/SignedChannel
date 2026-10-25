using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace SignedChannel;

/// <summary>
/// Registration for the channel's own services.
/// </summary>
public static class SignedChannelServiceCollectionExtensions
{
    /// <summary>
    /// Registers the channel: options, freshness, the two validators, the expiry policy, the
    /// action resolver, push and sign-in/out, and in-memory instruction and token stores unless
    /// others are registered.
    /// <para>
    /// A session store is <em>not</em> registered here — the choice of backing store is the
    /// application's. Register an <see cref="IChannelSessionStore"/> yourself, or call
    /// <c>AddSignedChannelMongoDb</c> from the <c>SignedChannel.MongoDb</c> package.
    /// </para>
    /// <para>
    /// Actions are not registered either: the dispatcher resolves them by namespace convention and
    /// instantiates them through <c>ActivatorUtilities</c>, so their constructor dependencies are
    /// injected without each action needing a DI entry.
    /// </para>
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">
    /// Sets the application-specific options. <see cref="SignedChannelOptions.ActionAssembly"/> and
    /// <see cref="SignedChannelOptions.RootNamespace"/> have no sensible default and must be set.
    /// </param>
    public static IServiceCollection AddSignedChannel(
        this IServiceCollection services,
        Action<SignedChannelOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        services.Configure(configure);
        services.PostConfigure<SignedChannelOptions>(options => options.Validate());

        services.AddSingleton<TimestampFreshness>();
        services.AddSingleton<SessionExpiryPolicy>();
        services.AddSingleton<ActionResolver>();
        services.AddSingleton<ConnectionRegistry>();
        services.AddSingleton<ChannelPushService>();
        services.AddSingleton<ChannelSessionService>();

        // Process-local defaults, so a single instance works with nothing else registered. Added
        // only if absent: SignedChannel.MongoDb (or the application) replaces them for anything
        // running more than one instance, in either registration order.
        services.TryAddSingleton<IChannelInstructionStore, InMemoryChannelInstructionStore>();
        services.TryAddSingleton<IChannelTokenStore, InMemoryChannelTokenStore>();

        services.AddScoped<PublicKeysRegistrationRequestValidatorV1>();
        services.AddScoped<SignedPayloadRequestValidatorV1>();

        return services;
    }

    /// <summary>
    /// Binds <see cref="FreshnessOptions"/> and <see cref="SessionExpiryOptions"/> from
    /// configuration sections, so the replay window and session lifetimes can be set per
    /// environment.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Configuration to bind from.</param>
    /// <param name="freshnessSection">Section holding <see cref="FreshnessOptions"/>.</param>
    /// <param name="sessionExpirySection">Section holding <see cref="SessionExpiryOptions"/>.</param>
    public static IServiceCollection BindSignedChannelConfiguration(
        this IServiceCollection services,
        IConfiguration configuration,
        string freshnessSection = "AuthFreshness",
        string sessionExpirySection = "SessionExpiry")
    {
        services.Configure<FreshnessOptions>(configuration.GetSection(freshnessSection));
        services.Configure<SessionExpiryOptions>(configuration.GetSection(sessionExpirySection));
        return services;
    }
}
