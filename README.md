# SignedChannel

A session-bound **signed channel** between a browser and an ASP.NET Core server:

- **Browser → server** is signed HTTP. Every request carries a signature made with a key pair
  generated per session, so a request cannot be forged or replayed.
- **Server → browser** is SignalR push, on a presence-tracked connection — so the server knows
  when a tab goes away.
- **Business logic is actions, not controllers.** You write classes implementing
  `ISecureWebBrowserAction<TRequest, TResponse>`; they are resolved by wire name through a
  namespace convention. No route table, no per-endpoint auth plumbing.

| Package | Registry | What it is |
| --- | --- | --- |
| `SignedChannel` | NuGet | The server half: handshake, dispatcher, presence hub, push |
| `SignedChannel.MongoDb` | NuGet | MongoDB session storage |
| `signed-channel-client` | npm | The browser half, framework-agnostic |

All three version in lockstep from one git tag, so the client and server halves of the protocol
cannot drift apart.

## Getting started

```csharp
builder.Services.BindSignedChannelConfiguration(builder.Configuration);
builder.Services.AddSignedChannel(options =>
{
    options.ActionAssembly = typeof(Program).Assembly;
    options.RootNamespace  = "MyApp";
    options.DevActionPrefix = "MyApp.Dev.";   // keep the trailing dot
});
builder.Services.AddSignedChannelMongoDb();   // or your own IChannelSessionStore

app.MapSignedChannelHub();   // must come before any SPA fallback route
app.MapSignedChannel();
```

`ActionAssembly` and `RootNamespace` have no sensible default and are validated at startup.
Action lookup scans the assembly you name — if it scanned its own, it would find nothing and
every action would answer 404 with nothing in the logs to say why.

An action is just a class in the right place. The wire name `"Profile.GetName"` resolves to
`MyApp.Profile.Actions.WebApp.GetName.GetNameAction`:

```csharp
public sealed class GetNameAction : ISecureWebBrowserAction<GetNameRequest, GetNameResponse>
{
    // Constructor dependencies are injected — no DI registration needed.
    public WebBrowserActionsValidationResult ValidateFromWebBrowser(...) => new() { IsValid = true };
    public Task<bool> HasAccess(...) => Task.FromResult(userId is not null);
    public async Task<GetNameResponse> ProcessMessageFromWebBrowserAsync(...) { ... }
}
```

### Signing a session in

The channel never decides who someone is. Authenticate however you like — passkeys, OIDC, a
magic link — then tell it:

```csharp
await sessionStore.SetUserIdAsync(sessionId, yourOwnUserId);
```

The user id is opaque: echoed to actions, used to decide whether a request counts as signed in,
never interpreted. That is what lets a confidential OIDC client hand over the `sub` from its own
callback and get an authenticated channel out of it.

### Optional hooks

Register any of these and the channel calls them; leave them out and it does not.

| Interface | Called for |
| --- | --- |
| `IChannelSessionObserver` | a session registering — per-browser bookkeeping |
| `IChannelActivityRecorder` | a completed action — your audit trail |
| `ISessionDeviceResolver` | the device behind a signed-in session |
| `IPresenceObserver` | connections opening and closing |
| `IConnectionChangeNotifier` | a signed-in session losing a tab |

## Status

**Early — `0.x`, API not yet stable.** The code is extracted from a production implementation
rather than written fresh, so the protocol is settled even while the C# surface still moves.

## Licence

Apache-2.0. See [LICENSE](LICENSE) and [NOTICE](NOTICE).
