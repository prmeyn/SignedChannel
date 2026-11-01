# SignedChannel

A session-bound **signed channel** between a browser and an ASP.NET Core server:

- **Browser → server** is signed HTTP. Every request carries a signature made with a key pair
  generated per session, so a request cannot be forged or replayed.
- **Server → browser** is SignalR push, on a presence-tracked connection — so the server knows
  when a tab goes away, and can send your application's own events to the tabs that are there.
- **Business logic is actions, not controllers.** You write classes implementing
  `ISecureWebBrowserAction<TRequest, TResponse>`; they are resolved by wire name through a
  namespace convention. No route table, no per-endpoint auth plumbing.

| Package | Registry | What it is |
| --- | --- | --- |
| `SignedChannel` | NuGet | The server half: handshake, dispatcher, presence hub, push, sign-in/out |
| `SignedChannel.MongoDb` | NuGet | MongoDB storage for sessions, instructions and tokens |
| `signed-channel-client` | npm | The browser half, framework-agnostic |

All three version in lockstep from one git tag, so the client and server halves of the protocol
cannot drift apart.

## Getting started

**Server** (`Program.cs`):

```csharp
builder.Services.BindSignedChannelConfiguration(builder.Configuration);
builder.Services.AddSignedChannel(options =>
{
    options.ActionAssembly  = typeof(Program).Assembly;   // where your actions live
    options.RootNamespace   = "MyApp";
    options.DevActionPrefix = "MyApp.Dev.";               // keep the trailing dot
});
builder.Services.AddSignalR();
builder.Services.AddSignedChannelMongoDb();   // sessions, instructions and tokens on MongoDB

app.MapSignedChannelHub();   // both must come before any SPA fallback route
app.MapSignedChannel();
```

`ActionAssembly` and `RootNamespace` have no sensible default and are validated at startup.
Without `AddSignedChannelMongoDb` you must register your own `IChannelSessionStore`; the
instruction and token stores then default to in-memory, which is fine for one instance and wrong
for more than one.

**Browser:**

```ts
import { ChannelCore, CryptoCore, SessionApi, SessionStore, SignalRChannelConnection } from 'signed-channel-client';
import * as signalR from '@microsoft/signalr';   // or the global from signalr.min.js

const crypto = new CryptoCore();
const store = new SessionStore();
const connection = new SignalRChannelConnection({ signalR });
const channel = new ChannelCore(connection, new SessionApi(crypto, store), store, crypto);
channel.onSignedOut = () => location.assign('/sign-in');
channel.start('en');
```

On start the channel registers a session, proves it owns the connection (an encrypted challenge
only that session's key can read) and joins the session's group. All of that is built in; wire
names under `Channel.` belong to the package, and an application action that would answer one
fails startup.

## Browser → server: actions

An action is a class in the right place. The wire name `"Orders.Ship"` resolves to
`MyApp.Orders.Actions.WebApp.Ship.ShipAction`:

```csharp
namespace MyApp.Orders.Actions.WebApp.Ship;

public sealed record ShipRequest : MessageWebBrowserRequestBase
{
    [JsonPropertyName("orderId")] public required string OrderId { get; init; }
}

public sealed class ShipAction : ISecureWebBrowserAction<ShipRequest, MessageWebBrowserResponseBase>
{
    private readonly OrderService _orders;

    // Constructor dependencies are injected — no DI registration needed.
    public ShipAction(OrderService orders) => _orders = orders;

    public WebBrowserActionsValidationResult ValidateFromWebBrowser(string sessionId, string? userId, string? latestDeviceId, ShipRequest? request) =>
        new() { IsValid = !string.IsNullOrWhiteSpace(request?.OrderId) };

    public Task<bool> HasAccess(HttpRequest httpRequest, string sessionId, string? userId, string? latestDeviceId, ShipRequest? request) =>
        Task.FromResult(userId is not null);   // signed in

    public async Task<MessageWebBrowserResponseBase> ProcessMessageFromWebBrowserAsync(
        HttpRequest httpRequest, string sessionId, string signalRConnectionId, string? userId, string? latestDeviceId,
        ShipRequest? request, string jobId, CancellationToken cancellationToken)
    {
        await _orders.ShipAsync(request!.OrderId);
        return new MessageWebBrowserResponseBase();
    }
}
```

```ts
const response = await channel.send('Orders.Ship', { orderId: 'o-42' });
```

`send` signs the request and adds the page context (`currentUrl`, `referrerUrl`). A rejected
request (validation, access, unknown action) resolves to an empty object rather than throwing, so
optional-chain the result.

## Server → browser: custom events

Your application's events are called **instructions**: a name you choose and an optional payload
(usually JSON). There is nothing to declare or register on either side — push a name from the
server, listen for the same name in the browser.

### 1. Push from the server

Inject `ChannelPushService` and call `PushInstructionAsync` with the session to tell. Inside an
action, the session and the calling tab's connection are parameters:

```csharp
public sealed class ShipAction : ISecureWebBrowserAction<ShipRequest, MessageWebBrowserResponseBase>
{
    private readonly OrderService _orders;
    private readonly ChannelPushService _push;

    public ShipAction(OrderService orders, ChannelPushService push)
    {
        _orders = orders;
        _push = push;
    }

    // ValidateFromWebBrowser and HasAccess as above.

    public async Task<MessageWebBrowserResponseBase> ProcessMessageFromWebBrowserAsync(
        HttpRequest httpRequest, string sessionId, string signalRConnectionId, string? userId, string? latestDeviceId,
        ShipRequest? request, string jobId, CancellationToken cancellationToken)
    {
        var order = await _orders.ShipAsync(request!.OrderId);

        await _push.PushInstructionAsync(
            sessionId,
            "Orders.Shipped",                                    // your event name
            JsonSerializer.Serialize(new { order.Id, order.TrackingNumber }),
            validity: TimeSpan.FromMinutes(1),                   // how long the tabs have to collect it
            exceptConnectionId: signalRConnectionId);            // optional: skip the tab that asked

        return new MessageWebBrowserResponseBase();
    }
}
```

Anywhere else — a background job, a webhook, another user's action — works the same way, as long
as you know the session id:

```csharp
public sealed class CourierWebhook(ChannelPushService push, MySessionIndex sessions)
{
    public async Task OnDeliveredAsync(string userId, string orderId)
    {
        foreach (var sessionId in await sessions.LiveSessionsOfAsync(userId))
        {
            await push.PushInstructionAsync(sessionId, "Orders.Delivered",
                JsonSerializer.Serialize(new { orderId }), TimeSpan.FromMinutes(5));
        }
    }
}
```

The channel does not keep a user → sessions index: who should hear about what is your
application's business. The natural place to record it is where you sign a session in (below),
since that is the moment you know both ids.

### 2. Listen in the browser

```ts
const unsubscribe = channel.on('Orders.Shipped', (payload) => {
  const { id, trackingNumber } = JSON.parse(payload!);
  showToast(`Order ${id} shipped — ${trackingNumber}`);
});

// When the page or component goes away:
unsubscribe();
```

The handler receives the payload exactly as the server passed it — a string, or null when there
was none. Any number of handlers may listen to one name; a name nobody listens to is ignored.
In Angular, call `on` in a service or component and pass the returned function to
`DestroyRef.onDestroy`.

### How delivery works, and its limits

- The instruction is stored on the server and only a short pointer is pushed over SignalR,
  encrypted to the session's key (RSA payloads are size-limited; this way the payload is not).
  The browser then downloads the instruction over the signed channel. Only the session it was
  addressed to can collect it.
- **It reaches the tabs that are connected and verified at that moment.** Nothing is queued for
  tabs that open later, and an instruction nobody collected is discarded when `validity` runs
  out. For state that must not be missed, have the page re-read it (with an action) when it loads,
  and treat instructions as "something changed, look again".
- Delivery is best-effort, like any push: keep payloads small and idempotent to apply, and do not
  rely on ordering between different instructions.
- Names starting with `Channel.` are reserved for the protocol.

## Signing a session in and out

The channel never decides who someone is. Authenticate however you like — passkeys, OIDC, a
magic link — then tell it, through `ChannelSessionService`:

```csharp
await channelSessions.SignInAsync(sessionId, yourOwnUserId);
await mySessionIndex.AddAsync(yourOwnUserId, sessionId);   // if you push to users later
```

That pushes `Channel.SignedIn` to every tab of the session, where `channel.onSignedIn` fires and
`channel.isSignedIn` becomes true. It returns `false`, and signs nothing in, when the session is
gone or was signed out.

**Signing out covers the browser.** Each tab is its own session, but a person signs out of a
browser, not a tab: `channel.signOut()` (the built-in `Channel.SignOut`) and the server's
`SignOutBrowserAsync(sessionId)` end **every** signed-in session of that person in that browser,
and each of their tabs ends in `channel.onSignedOut`. Someone else signed in elsewhere in the same
browser is left alone. `SignOutAsync(sessionId)` still ends a single session.

A browser's sessions share one secret: the HttpOnly `session_secret` cookie every signed-in request
is checked against, so a signing key stolen by script is useless outside that browser. Registration
reuses the cookie the browser already has, a failed check refuses the request without deleting it,
and sign-out leaves it in place for the browser's other sessions.

**Sign-out is final.** A signed-out session is ended for good: every request on it except
`Channel.SignOut` gets `401 {"code":"session_ended"}`, and the client answers by dropping its keys
and calling `onSignedOut`, so the browser's next sign-in happens on a **new** session. This covers
the tab that missed the `Channel.SignedOut` push (asleep, or reconnecting): it can no longer sign
the old session in again, which would revive keys that were signed out and run two sign-ins — or
two people — through one session id.

The user id is opaque: echoed to actions, used to decide whether a request counts as signed in,
never interpreted. That is what lets a confidential OIDC client hand over the `sub` from its own
callback and get an authenticated channel out of it.

Signed-in sessions expire after inactivity and at an absolute limit (`SessionExpiry` section of
configuration). Every signed-in response carries the expiry, mirrored on the client as
`channel.sessionExpiresAtUtc` / `onExpiryChange`; `channel.keepAlive()` extends it.

## Optional hooks

Register any of these and the channel calls them; leave them out and it does not.

| Interface | Called for |
| --- | --- |
| `IChannelSessionObserver` | a session registering — per-browser bookkeeping |
| `IChannelActivityRecorder` | a completed action — your audit trail |
| `ISessionDeviceResolver` | the device behind a signed-in session |
| `IPresenceObserver` | connections opening, binding to a session, and closing |
| `IConnectionChangeNotifier` | a signed-in session gaining or losing a tab |
| `IChannelSignOutObserver` | a browser signing out — your own cookies, a companion device |

## Versioning

The major version follows the .NET version it targets (10.x is .NET 10), so it is **not** a
breaking-change signal: a minor release can change the API, and the release notes say so. Both
halves always release together under one version; pin the pair exactly (`10.2.1`, not `^10.2.0`)
and read the release notes before moving.

**10.7.0** — the browser client reports `navigator.maxTouchPoints` at registration, and
`SessionRegisteredContext.MaxTouchPoints` hands it to the host: 0 on a Mac, 5 on an iPad, whose Safari
sends a Mac user-agent word for word. Optional on the wire (older clients omit it) and in the context.
Update the npm client and the NuGet packages together.

**10.6.0** — `SessionRegisteredContext.UserAgentClientHint`: the browser's `Sec-CH-UA` client hint,
alongside `UserAgent`. It names browsers whose user-agent is Chrome's word for word, such as Brave;
Firefox and Safari do not send it. A new optional last parameter, so existing code compiles.

**10.5.0** — an expired session is stamped with **when it expired**, not when something noticed.
Expiry is computed rather than written, so an abandoned session used to get the time of its next
request, or of a browser-wide sign-out hours later — which made it look as if it had lasted until then.
- `SignOutBrowserAsync` (and so `Channel.SignOut`) no longer signs out sessions that have already
  expired: they are stamped with their expiry, without a `SignedOut` push, and not counted.
- The dispatcher's `session_expired` path stamps the expiry.
- `IChannelSessionStore` gained `SetLogoutAsync(sessionId, loggedOutAt)`, with a default that stamps
  the current time, so custom stores compile and behave as before until they override it.
- **The first stamp wins:** `SetLogoutAsync` no longer overwrites an existing sign-out time
  (`MongoChannelSessionStore` used to, so signing out an ended session moved its end). If you implement
  the store yourself, filter on `LogoutDateTimeOffset == null` too.
- `ChannelSessionService`'s constructor also takes `SessionExpiryPolicy` (already registered).

**10.4.0** —
- **One `session_secret` per browser**, shared by its sessions. Each registration used to replace the
  browser's cookie, so opening a tab broke every other signed-in tab's next request — and the failed
  check deleted the cookie, taking the new tab down too.
- **Sign-out covers the browser:** `Channel.SignOut` and the new `ChannelSessionService.SignOutBrowserAsync`
  end all signed-in sessions of that person in that browser. `IChannelSessionStore` gained
  `GetSignedInSessionIdsOfBrowserAsync`, with a default that returns the session alone, so custom stores
  compile and keep signing out one session until they override it.
- **Back/forward cache:** the browser client closes its connection on every page hide, including a
  page frozen into the cache (Chrome kept those connections, and so the server's tab counts, alive
  for 10 minutes), and reconnects on restore. New `ChannelCore.onConnectionVerified`, fired for every
  verified connection.

**10.3.0** — a signed-out session is ended for good (see *Signing a session in and out*).
`ChannelSessionService.SignInAsync` now returns `Task<bool>`. If you implement `IChannelSessionStore`
yourself, `SetUserIdAsync` must no longer clear `LogoutDateTimeOffset` — the opposite of what it
had to do before.

The code is extracted from a production implementation (uSignIn) rather than written fresh.

## Licence

Apache-2.0. See [LICENSE](LICENSE) and [NOTICE](NOTICE).
