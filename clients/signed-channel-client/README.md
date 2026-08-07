# signed-channel-client

Browser half of the [SignedChannel](https://github.com/prmeyn/SignedChannel): a session-bound
signed request channel to an ASP.NET Core server, with SignalR push coming back the other way.

Framework-agnostic — no Angular, no React, no bundler assumptions. The connection is abstracted
behind `ChannelConnection`, so the same channel runs under a framework service, a plain script on
a server-rendered page, or a test harness.

```bash
npm install signed-channel-client
```

## Status

**Early — `0.x`, API not yet stable.** This version publishes the shared contracts only: the
`ChannelConnection` abstraction, the `PushMessage` envelope, and the base64 helpers whose exact
byte handling the signing protocol depends on. The channel itself — handshake, request signing,
push decryption, session expiry — lands next, extracted from a production implementation rather
than written fresh.

The server half is the `SignedChannel` NuGet package.

## Licence

Apache-2.0.
