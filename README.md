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
builder.Services.AddSignalR();
builder.Services.AddSignedChannelMongoDb();   // sessions, instructions and tokens on MongoDB

app.MapSignedChannelHub();   // must come before any SPA fallback route
app.MapSignedChannel();
```

Without `AddSignedChannelMongoDb` you still need an `IChannelSessionStore`; the instruction and
token stores default to in-memory, which is fine for one instance and wrong for more than one.

```ts
import { ChannelCore, CryptoCore, SessionApi, SessionStore, SignalRChannelConnection } from 'signed-channel-client';

const crypto = new CryptoCore();
const store = new SessionStore();
const connection = new SignalRChannelConnection({ signalR });
const channel = new ChannelCore(connection, new SessionApi(crypto, store), store, crypto);
channel.start('en');
```

The channel's own handshake — registering the connection, the encrypted connection challenge,
keep-alive, sign-out — is built in. Wire names under `Channel.` belong to the package, and an
application action that would answer one fails startup.

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

On the browser, `channel.send('Profile.GetName', { ... })` signs and posts it.

### Custom events: instructions

The server tells a session's tabs something by pushing an **instruction** — a name your
application defines and a payload of any size. Inject `ChannelPushService` anywhere you have a
session id: in an action, a background job, a webhook handler.

```csharp
await push.PushInstructionAsync(sessionId, "Orders.Shipped", JsonSerializer.Serialize(order),
    TimeSpan.FromMinutes(1),
    exceptConnectionId: signalRConnectionId);   // optional: skip the tab that caused it
```

```ts
const unsubscribe = channel.on('Orders.Shipped', (payload) => showToast(JSON.parse(payload!)));
```

Under the hood the instruction is stored, only a short pointer is pushed — encrypted to the
session's key, because RSA payloads are size-limited — and the browser downloads the instruction
over the signed channel. Each pointer is single-use and only the session it was addressed to can
collect it.

Finding *which* sessions to push to (all of a user's tabs, say) is the application's business:
keep the ids you need, for instance from `IChannelSessionObserver`.

### Signing a session in

The channel never decides who someone is. Authenticate however you like — passkeys, OIDC, a
magic link — then tell it:

```csharp
await channelSessions.SignInAsync(sessionId, yourOwnUserId);   // ChannelSessionService
```

That clears any earlier sign-out and pushes `Channel.SignedIn` to every tab of the session, where
`channel.onSignedIn` fires. `SignOutAsync(sessionId)` is the reverse, for sign-outs that start on
the server; a tab signs itself out with `channel.signOut()`, and every tab ends in
`channel.onSignedOut`.

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
| `IPresenceObserver` | connections opening, binding to a session, and closing |
| `IConnectionChangeNotifier` | a signed-in session gaining or losing a tab |
| `IChannelSignOutObserver` | a browser signing out — your own cookies, a companion device |

## Status

**API not yet stable.** The code is extracted from a production implementation rather than
written fresh, so the protocol is settled even while the surface still moves.

**Versioning:** the major version follows the .NET version it targets (10.x is .NET 10), so it is
*not* a breaking-change signal — a minor release can break the API. Both halves always release
together under one version; pin the pair exactly (`10.2.0`, not `^10.1.0`) and read the release
notes before moving.

## Licence

Apache-2.0. See [LICENSE](LICENSE) and [NOTICE](NOTICE).
