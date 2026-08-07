# signed-channel-client

Browser half of the [SignedChannel](https://github.com/prmeyn/SignedChannel): a session-bound
signed request channel to an ASP.NET Core server, with SignalR push coming back the other way.

Framework-agnostic — no Angular, no React, no bundler assumptions. The connection is abstracted
behind `ChannelConnection`, so the same channel runs under a framework service, a plain script on
a server-rendered page, or a test harness.

```bash
npm install signed-channel-client
```

## What's in it

- `AuthChannelCore` — the channel: handshake, request signing, push decryption, session expiry.
- `SessionApi`, `SessionStore`, `CryptoCore` — the pieces it runs on.
- `SignalRChannelConnection` — a `ChannelConnection` over SignalR, with automatic reconnect.
- `ChannelConnection`, `PushMessage`, and the base64 helpers.

**SignalR is injected, not imported**, so this package depends on nothing. A bundled host passes
its `@microsoft/signalr` import; a page that loads `signalr.min.js` from a script tag passes the
global. Both get identical behaviour, and the script-tag host doesn't bundle a second copy.

```ts
import { AuthChannelCore, SessionApi, SessionStore, CryptoCore, SignalRChannelConnection } from 'signed-channel-client';

const crypto = new CryptoCore();
const store = new SessionStore();
const connection = new SignalRChannelConnection({ signalR, onStatusChange: (s) => render(s) });
new AuthChannelCore(connection, new SessionApi(crypto, store), store, crypto).start('en');
```

## Status

**Early — `0.x`, API not yet stable.** Extracted from a production implementation rather than
written fresh, but the surface may still move.

The server half is the `SignedChannel` NuGet package.

## Licence

Apache-2.0.
