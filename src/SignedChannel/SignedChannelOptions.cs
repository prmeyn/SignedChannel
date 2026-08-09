using System.Reflection;

namespace SignedChannel;

/// <summary>
/// Host-supplied configuration for the channel. Everything here describes the *application*
/// rather than the protocol, which is why it cannot be defaulted inside the library.
/// </summary>
public sealed class SignedChannelOptions
{
    /// <summary>
    /// The assembly whose <see cref="ISecureWebBrowserAction{TRequest, TResponse}"/> implementations
    /// the dispatcher resolves.
    /// <para>
    /// This has to be supplied. Action lookup used to call <c>Assembly.GetExecutingAssembly()</c>,
    /// which inside a library resolves to the library — so every lookup would come back empty and
    /// every action would answer 404, with nothing in the logs to say why.
    /// </para>
    /// </summary>
    public Assembly? ActionAssembly { get; set; }

    /// <summary>
    /// The namespace actions are rooted at. The wire name <c>"A.B"</c> resolves to
    /// <c>{RootNamespace}.A.Actions.WebApp.B.BAction</c>.
    /// </summary>
    public string? RootNamespace { get; set; }

    /// <summary>
    /// Namespace prefix marking test-only actions, which are served in the Development environment
    /// and answered as "not found" everywhere else.
    /// <para>
    /// Include the trailing dot. <c>"MyApp.Dev."</c> gates <c>MyApp.Dev.Whatever</c> without also
    /// catching an unrelated <c>MyApp.Devices</c> namespace. Leave null to disable the gate, which
    /// is only correct if the application has no such actions at all — a shim that skips real
    /// verification is an account-takeover primitive if it is ever reachable in production.
    /// </para>
    /// </summary>
    public string? DevActionPrefix { get; set; }

    /// <summary>
    /// Name of the HttpOnly cookie holding the per-session secret whose hash is stored on the
    /// session record. Bound to authenticated sessions only.
    /// </summary>
    public string SessionCookieName { get; set; } = "session_secret";

    /// <summary>How long the session cookie lives.</summary>
    public TimeSpan SessionCookieLifetime { get; set; } = TimeSpan.FromDays(1);

    /// <summary>Version tag stamped on new session records.</summary>
    public string ApiVersion { get; set; } = "v1";

    /// <summary>Route of the session handshake.</summary>
    public string RegisterRoute { get; set; } = "/api/session/register";

    /// <summary>Route of the signed-action dispatcher.</summary>
    public string ActionRoute { get; set; } = "/api/action";

    /// <summary>Route the presence hub is mapped at.</summary>
    public string PresenceHubRoute { get; set; } = "/hubs/presence";

    /// <summary>
    /// Throws if the host left a required option unset. Called during startup rather than on the
    /// first request, so a misconfiguration fails at boot instead of as a puzzling 404 later.
    /// </summary>
    public void Validate()
    {
        if (ActionAssembly is null)
        {
            throw new InvalidOperationException(
                $"{nameof(SignedChannelOptions)}.{nameof(ActionAssembly)} must be set to the assembly containing your actions " +
                $"(for example typeof(Program).Assembly). Without it no action can be resolved.");
        }

        if (string.IsNullOrWhiteSpace(RootNamespace))
        {
            throw new InvalidOperationException(
                $"{nameof(SignedChannelOptions)}.{nameof(RootNamespace)} must be set to the namespace your actions are rooted at.");
        }

        if (DevActionPrefix is { Length: > 0 } prefix && !prefix.EndsWith('.'))
        {
            throw new InvalidOperationException(
                $"{nameof(SignedChannelOptions)}.{nameof(DevActionPrefix)} must end with a dot ('{prefix}.'), " +
                $"otherwise it also matches namespaces that merely start with the same letters.");
        }
    }
}
