/**
 * A server→client push envelope.
 *
 * The payload is encrypted with the session's public key, so a push is readable only by the
 * session it was addressed to — the socket carries ciphertext, and the server does not have to
 * trust the transport with the contents.
 */
export interface PushMessage {
  /** Names what the push is; the client dispatches on this. */
  actionName: string;

  /** The encrypted payload, base64-encoded. */
  encryptedPayloadAsBase64: string;

  /** When this message stops being valid (ISO 8601, UTC). */
  expiryTimestampInUtc: string;
}
