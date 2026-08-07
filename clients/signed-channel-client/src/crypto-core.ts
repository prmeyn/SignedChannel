import { base64ToBytes, toBase64 } from './base64.js';

/**
 * WebCrypto operations for the signed channel — framework-agnostic. Algorithms
 * must match the server (PublicKeyUtils): ECDSA P-384 signing, RSA-OAEP 2048 /
 * SHA-256 encryption. Do not change them.
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
      true,
      ['encrypt', 'decrypt']
    );
  }

  async generateSigningKeyPair(): Promise<CryptoKeyPair> {
    return window.crypto.subtle.generateKey(
      { name: 'ECDSA', namedCurve: 'P-384' },
      true,
      ['sign', 'verify']
    );
  }

  async exportPublicKey(keyPair: CryptoKeyPair): Promise<JsonWebKey> {
    return crypto.subtle.exportKey('jwk', keyPair.publicKey);
  }

  async exportPrivateKey(keyPair: CryptoKeyPair): Promise<JsonWebKey> {
    return crypto.subtle.exportKey('jwk', keyPair.privateKey);
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

  async importSigningPrivateKey(privateKeyData: JsonWebKey): Promise<CryptoKey> {
    return crypto.subtle.importKey('jwk', privateKeyData, { name: 'ECDSA', namedCurve: 'P-384' }, true, ['sign']);
  }

  async importDecryptionPrivateKey(privateKeyData: JsonWebKey): Promise<CryptoKey> {
    return crypto.subtle.importKey('jwk', privateKeyData, { name: 'RSA-OAEP', hash: { name: 'SHA-256' } }, true, ['decrypt']);
  }

  async decryptEncryptedStringAsBase64(encryptedStringAsBase64: string, privateKeyData: JsonWebKey): Promise<string | null> {
    try {
      const bytes = base64ToBytes(encryptedStringAsBase64);
      const importedKey = await this.importDecryptionPrivateKey(privateKeyData);
      return await this.decryptMessage(bytes as BufferSource, importedKey);
    } catch (e) {
      console.error('Decryption failed:', e);
      return null;
    }
  }

  base64Stringify(json: unknown): string {
    return toBase64(JSON.stringify(json));
  }
}
