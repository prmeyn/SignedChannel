// Runs against the built package (npm test builds first). Node has WebCrypto but no
// IndexedDB or Web Storage, so storage is shimmed and IndexedDB is absent — which is
// exactly the in-memory fallback path. The IndexedDB path needs a real browser.
import { test, beforeEach } from 'node:test';
import assert from 'node:assert/strict';
import { AuthChannelCore, CryptoCore, SessionApi, SessionStore, fromBase64, bytesToBase64, base64ToBytes } from '../dist/index.js';

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
  const channel = new AuthChannelCore(connection, new SessionApi(crypto, store), store, crypto);
  channel.start('en');
  onConnected();
  await new Promise((r) => setTimeout(r, 200));

  assert.equal(store.getSessionId(), 'fresh-session');
  assert.equal(store.getIsLoggedIn(), false);
  assert.equal(seen.actionSignatureValid, true); // RegisterConnectionAndGetStatus signed with the new key
});
