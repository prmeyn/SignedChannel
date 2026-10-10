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

## Key storage

Each tab's session has two private keys: ECDSA P-384 for signing requests, and RSA-OAEP for decrypting pushes.
Both are generated **non-extractable**. Scripts on the page, an XSS payload included, can use them in place
but can't read the key material out.

- The `CryptoKey` objects are stored in IndexedDB (database `signed-channel`, store `sessionKeys`) under a
  random per-tab id. `sessionStorage` holds that id and the session id, so each tab keeps its own session.
- Signing out (`resetKeyPairs`) deletes the tab's record. A tab closed while signed in leaves its record
  behind; a sweep on the next page load removes records older than `SessionStore.staleKeyRecordMaxAgeSeconds`
  (default 24 hours, which should stay above the server's absolute session lifetime).
- Where IndexedDB is unusable (some private-browsing modes), the keys live only in memory. A reload then
  finds a session id with no keys and registers a fresh session, so the user signs in again.
- Earlier versions stored the keys as extractable JWKs in `sessionStorage['sessionSettings']`. That entry is
  deleted on sight and never used; a tab that had one simply registers a new session.

Non-extractable keys stop key theft, not session riding: a script on the page can still sign requests,
and the browser attaches the server's HttpOnly session cookie to them. A strict Content-Security-Policy is
the defence against that.

## Status

**Early — `0.x`, API not yet stable.** Extracted from a production implementation rather than
written fresh, but the surface may still move.

The server half is the `SignedChannel` NuGet package.

## Licence

Apache-2.0.
