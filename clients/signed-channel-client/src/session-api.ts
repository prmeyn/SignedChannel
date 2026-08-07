import { bytesToBase64, toBase64 } from './base64.js';
import { CryptoCore } from './crypto-core.js';
import { SessionStore } from './session-store.js';

/**
 * The signed-channel client — framework-agnostic. Signing logic is preserved
 * exactly so the server's signature gate verifies; endpoints are same-origin.
 */
export class SessionApi {
  private static readonly HASH_ALGORITHM = 'SHA-512';
  private static readonly REGISTER_URL = '/api/session/register';
  private static readonly ACTION_URL = '/api/action';

  /** Fired on a 401 from a signed action, with the server's reason code when present
   * (e.g. "session_expired"). Lets the channel drive an idle-expiry sign-out. */
  onUnauthorized?: (code: string | null) => void;

  constructor(private readonly crypto: CryptoCore, private readonly settings: SessionStore) {}

  async registerSession(webBrowserId: string, connectionId: string, preferredLanguageIsoCode: string): Promise<string | null> {
    const signingKeyPair = await this.crypto.generateSigningKeyPair();
    const decryptionKeyPair = await this.crypto.generateEncryptionKeyPair();

    const verifyingPublicKeyData = await this.crypto.exportPublicKey(signingKeyPair);
    const signingPrivateKey = await this.crypto.exportPrivateKey(signingKeyPair);
    const decryptionPrivateKey = await this.crypto.exportPrivateKey(decryptionKeyPair);
    const encryptionPublicKeyData = await this.crypto.exportPublicKey(decryptionKeyPair);

    this.settings.saveSigningPrivateKey(signingPrivateKey);
    this.settings.saveDecryptingPrivateKey(decryptionPrivateKey);

    const VerifyingPublicKeyBase64 = this.crypto.base64Stringify(verifyingPublicKeyData);
    const EncryptionPublicKeyBase64 = this.crypto.base64Stringify(encryptionPublicKeyData);
    const TimeStampWithOffSetUTC = new Date().toISOString();

    const EncryptionPublicKeyRequestWithTimestampAsBase64 = this.crypto.base64Stringify({
      EncryptionPublicKeyBase64,
      TimeStampWithOffSetUTC,
    });

    const importedSigningPrivateKey = await this.crypto.importSigningPrivateKey(signingPrivateKey);
    const signature = await this.crypto.signMessage(
      SessionApi.HASH_ALGORITHM, EncryptionPublicKeyRequestWithTimestampAsBase64, importedSigningPrivateKey);

    const registrationRequest = {
      WebBrowserId: webBrowserId,
      SignalRConnectionId: connectionId,
      SelectedLanguageIsoCode: preferredLanguageIsoCode,
      VerifyingPublicKeyBase64,
      EncryptionPublicKeyRequestWithTimestampAsBase64,
      EncryptionPublicKeySignatureAsBase64: bytesToBase64(signature),
      HashAlgorithm: SessionApi.HASH_ALGORITHM,
    };

    const response = await fetch(SessionApi.REGISTER_URL, {
      method: 'POST',
      credentials: 'include',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(registrationRequest),
    });

    if (!response.ok) {
      this.settings.resetKeyPairs(true);
      return null;
    }

    const data = await response.json();
    if (!data?.encryptedSessionIdAsBase64) {
      return null;
    }
    const decryptingPrivateKey = this.settings.getDecryptingPrivateKey();
    if (!decryptingPrivateKey) {
      return null;
    }
    return this.crypto.decryptEncryptedStringAsBase64(data.encryptedSessionIdAsBase64, decryptingPrivateKey);
  }

  async sendSignedRequest<T = unknown>(actionName: string, payload: unknown = {}): Promise<T> {
    const SessionId = this.settings.getSessionId();
    const SignalRConnectionId = this.settings.getConnectionId();
    const TimeStampWithOffSetUTC = new Date().toISOString();
    const PayloadAsBase64 = payload != null ? toBase64(JSON.stringify(payload)) : null;

    const MessagePayloadRequestAsBase64 = toBase64(
      JSON.stringify({ SessionId, SignalRConnectionId, ActionName: actionName, TimeStampWithOffSetUTC, PayloadAsBase64 })
    );

    const signingPrivateKey = this.settings.getSigningPrivateKey();
    if (!signingPrivateKey) {
      return {} as T;
    }
    const importedSigningPrivateKey = await this.crypto.importSigningPrivateKey(signingPrivateKey);
    const signature = await this.crypto.signMessage(SessionApi.HASH_ALGORITHM, MessagePayloadRequestAsBase64, importedSigningPrivateKey);

    const signedPayloadRequest = {
      MessagePayloadRequestAsBase64,
      SignatureAsBase64: bytesToBase64(signature),
      HashAlgorithm: SessionApi.HASH_ALGORITHM,
    };

    const response = await fetch(SessionApi.ACTION_URL, {
      method: 'POST',
      credentials: 'include',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(signedPayloadRequest),
    });

    if (response.status === 401) {
      let code: string | null = null;
      try {
        code = ((await response.clone().json()) as { code?: string })?.code ?? null;
      } catch {
        // 401 with no JSON body (e.g. Results.Unauthorized()).
      }
      this.settings.resetKeyPairs();
      this.onUnauthorized?.(code);
      return {} as T;
    }
    if (!response.ok) {
      // A rejected action (400 validation, 404 unknown action, 500) is an ordinary outcome the
      // caller has to handle — every caller already optional-chains the result. Throwing here
      // skipped the `busy.set(false)` that follows each `await`, so one rejected submit left the
      // button stuck on "Saving…" until a reload. Report and resolve empty instead.
      console.error(`Action '${actionName}' failed: HTTP ${response.status}`);
      return {} as T;
    }
    return (await response.json()) as T;
  }
}
