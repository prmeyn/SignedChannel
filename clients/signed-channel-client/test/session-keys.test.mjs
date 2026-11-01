// Runs against the built package (npm test builds first). Node has WebCrypto but no
// IndexedDB or Web Storage, so storage is shimmed and IndexedDB is absent — which is
// exactly the in-memory fallback path. The IndexedDB path needs a real browser.
import { test, beforeEach } from 'node:test';
import assert from 'node:assert/strict';
import { ChannelCore, CryptoCore, SessionApi, SessionStore, SignalRChannelConnection, fromBase64, bytesToBase64, base64ToBytes } from '../dist/index.js';

class MemoryStorage {
  #items = new Map();
  getItem(key) { return this.#items.has(key) ? this.#items.get(key) : null; }
  setItem(key, value) { this.#items.set(key, String(value)); }
  removeItem(key) { this.#items.delete(key); }
  clear() { this.#items.clear(); }
  get size() { return this.#items.size; }
  dump() { return [...this.#items.values()].join('\n'); }
}

const subtle = globalThis.crypto.subtle;
globalThis.window = globalThis;
let navigations;
globalThis.location = { assign: (url) => navigations.push(url), href: 'http://localhost/test' };

beforeEach(() => {
  globalThis.sessionStorage = new MemoryStorage();
  globalThis.localStorage = new MemoryStorage();
  navigations = [];
});

/**
 * Plays the server's /api/session/register: checks the registration signature
 * against the submitted verifying key, then returns a session id encrypted to the
 * submitted encryption key — the same contract the real endpoint enforces.
 */
function mockRegisterServer(sessionId) {
  const seen = {};
  globalThis.fetch = async (url, init) => {
    const body = JSON.parse(init.body);
    if (url === '/api/session/register') {
      seen.maxTouchPoints = body.MaxTouchPoints;
      const verifyJwk = JSON.parse(fromBase64(body.VerifyingPublicKeyBase64));
      const verifyKey = await subtle.importKey('jwk', verifyJwk, { name: 'ECDSA', namedCurve: 'P-384' }, true, ['verify']);
      seen.verifyKey = verifyKey;
      seen.registrationSignatureValid = await subtle.verify(
        { name: 'ECDSA', hash: body.HashAlgorithm }, verifyKey,
        base64ToBytes(body.EncryptionPublicKeySignatureAsBase64),
        new TextEncoder().encode(body.EncryptionPublicKeyRequestWithTimestampAsBase64));
      const { EncryptionPublicKeyBase64 } = JSON.parse(fromBase64(body.EncryptionPublicKeyRequestWithTimestampAsBase64));
      const encJwk = JSON.parse(fromBase64(EncryptionPublicKeyBase64));
      const encKey = await subtle.importKey('jwk', encJwk, { name: 'RSA-OAEP', hash: 'SHA-256' }, true, ['encrypt']);
      const ciphertext = await subtle.encrypt({ name: 'RSA-OAEP' }, encKey, new TextEncoder().encode(sessionId));
      return new Response(JSON.stringify({ encryptedSessionIdAsBase64: bytesToBase64(new Uint8Array(ciphertext)) }), { status: 201 });
    }
    if (url === '/api/action') {
      seen.actionSignatureValid = await subtle.verify(
        { name: 'ECDSA', hash: body.HashAlgorithm }, seen.verifyKey,
        base64ToBytes(body.SignatureAsBase64),
        new TextEncoder().encode(body.MessagePayloadRequestAsBase64));
      return new Response(JSON.stringify({ ok: true }), { status: 200 });
    }
    throw new Error('unexpected fetch ' + url);
  };
  return seen;
}

test('generated private keys are non-extractable; public keys still export', async () => {
  const core = new CryptoCore();
  for (const pair of [await core.generateSigningKeyPair(), await core.generateEncryptionKeyPair()]) {
    assert.equal(pair.privateKey.extractable, false);
    await assert.rejects(subtle.exportKey('jwk', pair.privateKey));
    assert.ok((await core.exportPublicKey(pair)).kty);
  }
});

test('register + signed action round-trip; no key material in Web Storage', async () => {
  const seen = mockRegisterServer('session-123');
  const store = new SessionStore();
  const api = new SessionApi(new CryptoCore(), store);

  const sessionId = await api.registerSession('browser-1', 'conn-1', 'en');
  assert.equal(sessionId, 'session-123');
  assert.equal(seen.registrationSignatureValid, true);

  store.setSessionId(sessionId);
  const response = await api.sendSignedRequest('Some.Action', { a: 1 });
  assert.deepEqual(response, { ok: true });
  assert.equal(seen.actionSignatureValid, true);

  // The pre-change layout put JWKs (with the private "d" member) in sessionStorage.
  assert.equal(sessionStorage.getItem('sessionSettings'), null);
  assert.doesNotMatch(sessionStorage.dump() + localStorage.dump(), /"d"\s*:/);
  assert.equal((await store.getSigningPrivateKey()).extractable, false);
});

test('registration reports the touch points, which tell an iPad from a Mac', async () => {
  const original = Object.getOwnPropertyDescriptor(globalThis, 'navigator');
  Object.defineProperty(globalThis, 'navigator', { value: { maxTouchPoints: 5 }, configurable: true });
  try {
    const seen = mockRegisterServer('session-touch');
    await new SessionApi(new CryptoCore(), new SessionStore()).registerSession('browser-1', 'conn-1', 'en');
    assert.equal(seen.maxTouchPoints, 5);
    assert.equal(seen.registrationSignatureValid, true);
  } finally {
    if (original) {
      Object.defineProperty(globalThis, 'navigator', original);
    } else {
      delete globalThis.navigator;
    }
  }
});

test('without IndexedDB the keys are memory-only: a fresh page has none', async () => {
  mockRegisterServer('session-456');
  const store = new SessionStore();
  await new SessionApi(new CryptoCore(), store).registerSession('browser-1', 'conn-1', 'en');
  assert.ok(await store.getSigningPrivateKey());
  assert.equal(sessionStorage.getItem('sessionKeysId'), null);

  const afterReload = new SessionStore();
  assert.equal(await afterReload.getSigningPrivateKey(), undefined);
  assert.equal(await afterReload.getDecryptingPrivateKey(), undefined);
});

test('legacy extractable JWKs in sessionStorage are dropped, never used', async () => {
  const pair = await subtle.generateKey({ name: 'ECDSA', namedCurve: 'P-384' }, true, ['sign', 'verify']);
  sessionStorage.setItem('sessionSettings', JSON.stringify({ signingPrivateKey: await subtle.exportKey('jwk', pair.privateKey) }));
  sessionStorage.setItem('sessionStorageId', 'old-session');

  const store = new SessionStore();
  assert.equal(await store.getSigningPrivateKey(), undefined);
  assert.equal(sessionStorage.getItem('sessionSettings'), null);
});

test('resetKeyPairs clears keys synchronously, then navigates unless told not to', async () => {
  mockRegisterServer('session-789');
  const store = new SessionStore();
  await new SessionApi(new CryptoCore(), store).registerSession('browser-1', 'conn-1', 'en');
  store.setSessionId('session-789');

  const pending = store.resetKeyPairs(false, true);
  assert.equal(store.getSessionId(), null);
  assert.equal(await store.getSigningPrivateKey(), undefined);
  await pending;
  await new Promise((r) => setTimeout(r, 30));
  assert.deepEqual(navigations, []);

  await store.resetKeyPairs();
  await new Promise((r) => setTimeout(r, 30));
  assert.deepEqual(navigations, ['/']);
});

test('a 401 on a signed action clears the keys and reports the reason code', async () => {
  mockRegisterServer('session-401');
  const store = new SessionStore();
  const api = new SessionApi(new CryptoCore(), store);
  await api.registerSession('browser-1', 'conn-1', 'en');
  store.setSessionId('session-401');

  globalThis.fetch = async () => new Response(JSON.stringify({ code: 'session_expired' }), { status: 401 });
  let reported;
  api.onUnauthorized = (code) => { reported = code; };
  assert.deepEqual(await api.sendSignedRequest('Some.Action'), {});
  assert.equal(reported, 'session_expired');
  assert.equal(await store.getSigningPrivateKey(), undefined);
});

test('handshake re-registers when the stored session id has no keys behind it', async () => {
  const seen = mockRegisterServer('fresh-session');
  sessionStorage.setItem('sessionStorageId', 'orphaned-session');
  sessionStorage.setItem('isLoggedIn', 'true');

  let onConnected;
  const connection = {
    start() {},
    on() {},
    onConnected(cb) { onConnected = cb; },
    connectionId: () => 'conn-1',
  };
  globalThis.document = { referrer: '' };
  const crypto = new CryptoCore();
  const store = new SessionStore();
  const channel = new ChannelCore(connection, new SessionApi(crypto, store), store, crypto);
  channel.start('en');
  onConnected();
  await new Promise((r) => setTimeout(r, 200));

  assert.equal(store.getSessionId(), 'fresh-session');
  assert.equal(store.getIsLoggedIn(), false);
  assert.equal(seen.actionSignatureValid, true); // Channel.Register signed with the new key
});

/**
 * A fuller server stand-in: registration as above, then each signed action answered by name, and
 * pushes delivered the way SignalR would — encrypted to the session's own key.
 */
async function connectChannel(sessionId, actions) {
  const requests = [];
  let encryptionKey;
  globalThis.fetch = async (url, init) => {
    const body = JSON.parse(init.body);
    if (url === '/api/session/register') {
      const { EncryptionPublicKeyBase64 } = JSON.parse(fromBase64(body.EncryptionPublicKeyRequestWithTimestampAsBase64));
      encryptionKey = await subtle.importKey('jwk', JSON.parse(fromBase64(EncryptionPublicKeyBase64)), { name: 'RSA-OAEP', hash: 'SHA-256' }, true, ['encrypt']);
      return new Response(JSON.stringify({ encryptedSessionIdAsBase64: await encrypt(sessionId) }), { status: 201 });
    }
    const message = JSON.parse(fromBase64(body.MessagePayloadRequestAsBase64));
    const payload = message.PayloadAsBase64 ? JSON.parse(fromBase64(message.PayloadAsBase64)) : {};
    requests.push({ actionName: message.ActionName, payload });
    const answer = actions[message.ActionName]?.(payload) ?? {};
    // An action may answer with a status of its own: { status, body }.
    if (answer && typeof answer.status === 'number') {
      return new Response(JSON.stringify(answer.body ?? {}), { status: answer.status });
    }
    return new Response(JSON.stringify(answer), { status: 202 });
  };
  async function encrypt(text) {
    return bytesToBase64(new Uint8Array(await subtle.encrypt({ name: 'RSA-OAEP' }, encryptionKey, new TextEncoder().encode(text))));
  }

  let onConnected;
  let receive;
  const connection = {
    start() {},
    on(method, handler) { assert.equal(method, 'ReceiveEncryptedMessage'); receive = handler; },
    onConnected(cb) { onConnected = cb; },
    connectionId: () => 'conn-1',
  };
  globalThis.document = { referrer: '' };
  const crypto = new CryptoCore();
  const store = new SessionStore();
  const channel = new ChannelCore(connection, new SessionApi(crypto, store), store, crypto);
  channel.start('en');
  onConnected();
  // Key generation takes a variable while; wait for the handshake itself rather than a guess.
  for (let i = 0; i < 100 && !requests.some((r) => r.actionName === 'Channel.Register'); i++) {
    await new Promise((r) => setTimeout(r, 20));
  }
  assert.ok(encryptionKey, 'the session never registered');

  async function push(actionName, plaintext, expiresInMs = 60_000) {
    receive({
      actionName,
      encryptedPayloadAsBase64: plaintext == null ? '' : await encrypt(plaintext),
      expiryTimestampInUtc: new Date(Date.now() + expiresInMs).toISOString(),
    });
    await settle();
  }
  return { channel, store, requests, push };
}

const settle = () => new Promise((r) => setTimeout(r, 100));

test('the connection challenge is answered with the decrypted token, and verification binds the tab', async () => {
  const { channel, requests, push } = await connectChannel('s-challenge', { 'Channel.Register': () => ({ isSignedIn: false }) });
  assert.deepEqual(requests.map((r) => r.actionName), ['Channel.Register']);

  await push('Channel.ConnectionChallenge', 'the-token');
  await push('Channel.ConnectionVerified');

  assert.equal(requests[1].actionName, 'Channel.VerifyConnection');
  assert.equal(requests[1].payload.token, 'the-token');
  assert.equal(channel.channelBound, true);
});

test('an instruction is downloaded by its pointer and delivered to every handler for its name', async () => {
  const { channel, requests, push } = await connectChannel('s-instruction', {
    'Channel.Register': () => ({ isSignedIn: false }),
    'Channel.DownloadInstruction': ({ pointer }) =>
      pointer === 'msg_ott' ? { name: 'Orders.Shipped', payload: '{"orderId":"o-42"}' } : { name: null, payload: null },
  });
  const received = [];
  channel.on('Orders.Shipped', (payload) => received.push(['a', JSON.parse(payload).orderId]));
  const unsubscribe = channel.on('Orders.Shipped', (payload) => received.push(['b', JSON.parse(payload).orderId]));
  channel.on('Something.Else', () => received.push(['wrong']));

  await push('Channel.Instruction', 'msg_ott');
  unsubscribe();
  await push('Channel.Instruction', 'msg_ott');

  assert.deepEqual(requests.filter((r) => r.actionName === 'Channel.DownloadInstruction').map((r) => r.payload.pointer), ['msg_ott', 'msg_ott']);
  assert.deepEqual(received, [['a', 'o-42'], ['b', 'o-42'], ['a', 'o-42']]);
});

test('a spent pointer delivers nothing, and an expired push is ignored without a download', async () => {
  const { channel, requests, push } = await connectChannel('s-spent', {
    'Channel.Register': () => ({ isSignedIn: false }),
    'Channel.DownloadInstruction': () => ({ name: null, payload: null }),
  });
  let calls = 0;
  channel.on('Anything', () => calls++);

  await push('Channel.Instruction', 'spent_pointer');
  const downloadsAfterSpent = requests.length;
  // An envelope already past its expiry never reaches the server.
  await push('Channel.Instruction', 'late_pointer', -1_000);

  assert.equal(calls, 0);
  assert.equal(requests.at(-1).payload.pointer, 'spent_pointer');
  assert.equal(requests.length, downloadsAfterSpent);
});

test('a SignedIn push marks the tab signed in; a SignedOut push signs it out once', async () => {
  const { channel, store, requests, push } = await connectChannel('s-signin', { 'Channel.Register': () => ({ isSignedIn: false }) });
  let signedIn = 0;
  let signedOut = 0;
  channel.onSignedIn = () => signedIn++;
  channel.onSignedOut = () => signedOut++;

  await push('Channel.SignedIn');
  assert.equal(channel.isSignedIn, true);
  assert.equal(signedIn, 1);

  await push('Channel.SignedOut');
  await push('Channel.SignedOut');

  assert.equal(channel.isSignedIn, false);
  assert.equal(signedOut, 1);
  // The 401 itself already dropped the keys, so the sign-out confirmation cannot be signed and is
  // not sent — the server ended the session already, so there is nothing left to confirm.
  assert.equal(await store.getSigningPrivateKey(), undefined);
});

test('a session ended while this tab missed the push: the next 401 signs the tab out and drops its keys', async () => {
  const actions = { 'Channel.Register': () => ({ isSignedIn: true }) };
  const { channel, store } = await connectChannel('s-ended', actions);
  let signedOut = 0;
  channel.onSignedOut = () => signedOut++;

  actions['Some.Action'] = () => ({ status: 401, body: { code: 'session_ended' } });
  await channel.send('Some.Action');
  await settle();

  assert.equal(signedOut, 1);
  assert.equal(channel.isSignedIn, false);
  // The 401 itself already dropped the keys, so the sign-out confirmation cannot be signed and is
  // not sent — the server ended the session already, so there is nothing left to confirm.
  assert.equal(await store.getSigningPrivateKey(), undefined);
});

/** A SignalR stand-in that records start/stop and hands out a new connection id per start. */
function fakeSignalR() {
  const calls = [];
  let starts = 0;
  const hub = {
    connectionId: null,
    on() {},
    onreconnecting() {},
    onreconnected() {},
    onclose() {},
    async start() { calls.push('start'); hub.connectionId = `conn-${++starts}`; },
    async stop() { calls.push('stop'); hub.connectionId = null; },
  };
  class HubConnectionBuilder {
    withUrl() { return { withAutomaticReconnect: () => ({ build: () => hub }) }; }
  }
  return { signalR: { HubConnectionBuilder }, calls };
}

function pageTransition(type, persisted) {
  const event = new Event(type);
  Object.defineProperty(event, 'persisted', { value: persisted });
  return event;
}

test('every page hide stops the connection — a bfcache freeze too — and a bfcache restore reconnects', async () => {
  const previousWindow = globalThis.window;
  globalThis.window = new EventTarget();
  try {
    const { signalR, calls } = fakeSignalR();
    const connection = new SignalRChannelConnection({ signalR });
    const connectedIds = [];
    connection.onConnected(() => connectedIds.push(connection.connectionId()));
    connection.start();
    await settle();

    // Left for another page, with the browser keeping this one in its back/forward cache.
    window.dispatchEvent(pageTransition('pagehide', true));
    await settle();
    assert.deepEqual(calls, ['start', 'stop']);

    // Brought back with Back: a new connection, and onConnected again so the channel re-handshakes.
    window.dispatchEvent(pageTransition('pageshow', true));
    await settle();
    assert.deepEqual(calls, ['start', 'stop', 'start']);
    assert.deepEqual(connectedIds, ['conn-1', 'conn-2']);

    // A normal first page show starts nothing extra.
    window.dispatchEvent(pageTransition('pageshow', false));
    await settle();
    assert.deepEqual(calls, ['start', 'stop', 'start']);
  } finally {
    globalThis.window = previousWindow;
  }
});

test('onConnectionVerified fires for every verified connection, while channelBound flips once', async () => {
  const { channel, push } = await connectChannel('s-verified', { 'Channel.Register': () => ({ isSignedIn: false }) });
  let verified = 0;
  channel.onConnectionVerified = () => verified++;

  await push('Channel.ConnectionVerified');
  await push('Channel.ConnectionVerified');

  assert.equal(channel.channelBound, true);
  assert.equal(verified, 2);
});
