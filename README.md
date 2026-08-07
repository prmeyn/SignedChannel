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
| `signed-channel-client` | npm | The browser half, framework-agnostic |

## Status

**Early — `0.x`, API not yet stable.** These first versions publish the shared contracts only
(the action interface and message bases on the server; the connection abstraction, push envelope
and base64 helpers on the client). The transport and dispatcher land next, extracted from a
production implementation rather than written fresh.

## Licence

Apache-2.0. See [LICENSE](LICENSE) and [NOTICE](NOTICE).
