import { base64ToBytes, toBase64 } from './base64.js';

/**
 * WebCrypto operations for the signed channel — framework-agnostic. Algorithms
 * must match the server (PublicKeyUtils): ECDSA P-384 signing, RSA-OAEP 2048 /
 * SHA-256 encryption. Do not change them.
 *
 * Private keys are generated NON-extractable: page scripts (an XSS payload included)
 * can use them in place but can never read the key material out. For an asymmetric
 * pair `extractable` governs only the private key — the public halves stay exportable,
 * which is all registration needs.
 */
export class CryptoCore {
  async generateEncryptionKeyPair(): Promise<CryptoKeyPair> {
    return window.crypto.subtle.generateKey(
      {
        name: 'RSA-OAEP',
        modulusLength: 2048,
        publicExponent: new Uint8Array([0x01, 0x00, 0x01]),
        hash: { name: 'SHA-256' },
      },
      false,
      ['encrypt', 'decrypt']
    );
  }

  async generateSigningKeyPair(): Promise<CryptoKeyPair> {
    return window.crypto.subtle.generateKey(
      { name: 'ECDSA', namedCurve: 'P-384' },
      false,
      ['sign', 'verify']
    );
  }

  async exportPublicKey(keyPair: CryptoKeyPair): Promise<JsonWebKey> {
    return crypto.subtle.exportKey('jwk', keyPair.publicKey);
  }

  async signMessage(hashAlgorithm: string, message: string, privateKey: CryptoKey): Promise<Uint8Array> {
    const encodedMessage = new TextEncoder().encode(message);
    const signature = await crypto.subtle.sign({ name: 'ECDSA', hash: hashAlgorithm }, privateKey, encodedMessage);
    return new Uint8Array(signature);
  }

  async decryptMessage(ciphertext: BufferSource, privateKey: CryptoKey): Promise<string> {
    const decrypted = await crypto.subtle.decrypt({ name: 'RSA-OAEP' }, privateKey, ciphertext);
    return new TextDecoder().decode(decrypted);
  }

  async decryptEncryptedStringAsBase64(encryptedStringAsBase64: string, privateKey: CryptoKey): Promise<string | null> {
    try {
      const bytes = base64ToBytes(encryptedStringAsBase64);
      return await this.decryptMessage(bytes as BufferSource, privateKey);
    } catch (e) {
      console.error('Decryption failed:', e);
      return null;
    }
  }

  base64Stringify(json: unknown): string {
    return toBase64(JSON.stringify(json));
  }
}
