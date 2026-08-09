using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Options;

namespace SignedChannel;

/// <summary>
/// Everything reflection had to work out about an action, worked out once.
/// </summary>
/// <param name="Type">The action's concrete type, instantiated per request.</param>
/// <param name="RequestType">The action's request payload type, to deserialize into.</param>
/// <param name="ValidateMethod">Its validation method.</param>
/// <param name="AuthorizeMethod">Its authorization method.</param>
/// <param name="ProcessMethod">Its processing method.</param>
/// <param name="IsDevOnly">Whether it sits under the configured development-only namespace.</param>
public sealed record ResolvedAction(
    Type Type,
    Type RequestType,
    MethodInfo ValidateMethod,
    MethodInfo AuthorizeMethod,
    MethodInfo ProcessMethod,
    bool IsDevOnly);

/// <summary>
/// Maps a wire action name onto the class that implements it, by namespace convention:
/// <c>"A.B"</c> resolves to <c>{RootNamespace}.A.Actions.WebApp.B.BAction</c>.
/// <para>
/// The convention is the point — adding an action is adding a class, with no route to register
/// and no DI entry to remember. Results are cached, so the reflection cost is paid once per name.
/// </para>
/// </summary>
public sealed class ActionResolver
{
    private readonly ConcurrentDictionary<string, ResolvedAction?> _cache = new();
    private readonly SignedChannelOptions _options;
    private readonly Lazy<Type[]> _candidateTypes;

    /// <summary>Creates the resolver over the configured action assembly.</summary>
    public ActionResolver(IOptions<SignedChannelOptions> options)
    {
        _options = options.Value;
        _options.Validate();

        // Deferred: enumerating an assembly's types is not free, and an application that never
        // receives a request should not pay for it at startup.
        _candidateTypes = new Lazy<Type[]>(() => _options.ActionAssembly!.GetTypes());
    }

    /// <summary>
    /// The action for this wire name, or null if no class matches the convention. Callers should
    /// answer a null exactly as they answer an unknown name.
    /// </summary>
    public ResolvedAction? Resolve(string actionName) =>
        _cache.GetOrAdd(actionName, ResolveUncached);

    private ResolvedAction? ResolveUncached(string actionName)
    {
        var expectedFullName = $"{_options.RootNamespace}.{InsertActionsNamespace(actionName)}";

        var type = Array.Find(_candidateTypes.Value, candidate =>
            candidate.FullName == expectedFullName &&
            Array.Exists(candidate.GetInterfaces(), i =>
                i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ISecureWebBrowserAction<,>)));

        if (type is null)
        {
            return null;
        }

        var interfaceType = Array.Find(type.GetInterfaces(), i =>
            i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ISecureWebBrowserAction<,>))!;

        var validateMethod = interfaceType.GetMethod(nameof(ISecureWebBrowserAction<,>.ValidateFromWebBrowser));
        var authorizeMethod = interfaceType.GetMethod(nameof(ISecureWebBrowserAction<,>.HasAccess));
        var processMethod = interfaceType.GetMethod(nameof(ISecureWebBrowserAction<,>.ProcessMessageFromWebBrowserAsync));

        if (validateMethod is null || authorizeMethod is null || processMethod is null)
        {
            return null;
        }

        return new ResolvedAction(
            type,
            interfaceType.GetGenericArguments()[0],
            validateMethod,
            authorizeMethod,
            processMethod,
            IsDevOnly: IsDevAction(type));
    }

    /// <summary>
    /// Whether the type sits under the configured development-only namespace.
    /// <para>
    /// The prefix carries a trailing dot, so <c>"MyApp.Dev."</c> matches <c>MyApp.Dev.Anything</c>
    /// without also catching an unrelated <c>MyApp.Devices</c>.
    /// </para>
    /// </summary>
    private bool IsDevAction(Type type) =>
        _options.DevActionPrefix is { Length: > 0 } prefix &&
        type.Namespace?.StartsWith(prefix, StringComparison.Ordinal) == true;

    /// <summary>
    /// <c>"A.B"</c> → <c>"A.Actions.WebApp.B.BAction"</c>.
    /// </summary>
    private static string InsertActionsNamespace(string clientActionName)
    {
        var parts = clientActionName.Split('.');

        if (parts.Length >= 2)
        {
            var index = parts.Length - 1;
            var list = new List<string>(parts);
            list.Insert(index, "Actions.WebApp");
            list.Insert(parts.Length + 1, $"{parts[^1]}Action");
            parts = [.. list];
        }

        return string.Join(".", parts);
    }
}
