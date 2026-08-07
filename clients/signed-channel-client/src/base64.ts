// The exact byte handling matters: signatures and encrypted payloads must encode identically to
// what the server expects, so these are deliberately plain and unclever.
//
// Inputs here are small by construction — session keys, signatures, and JSON envelopes — which is
// why spreading into String.fromCharCode is safe. Do not reuse these for file-sized data.

/** Encodes a UTF-8 string to Base64. */
export function toBase64(plainText: string): string {
  if (typeof plainText !== 'string') {
    throw new TypeError('Input must be a string');
  }
  const bytes = new TextEncoder().encode(plainText);
  const binString = String.fromCharCode(...bytes);
  return btoa(binString);
}

/** Decodes a Base64 string to UTF-8. */
export function fromBase64(base64String: string): string {
  if (typeof base64String !== 'string') {
    throw new TypeError('Input must be a string');
  }
  const binString = atob(base64String);
  const bytes = Uint8Array.from(binString, (m) => m.charCodeAt(0));
  return new TextDecoder().decode(bytes);
}

/** Encodes binary data to Base64. */
export function bytesToBase64(data: Uint8Array): string {
  if (!(data instanceof Uint8Array)) {
    throw new TypeError('Input must be a Uint8Array');
  }
  const binString = String.fromCharCode(...data);
  return btoa(binString);
}

/** Decodes a Base64 string to binary data. */
export function base64ToBytes(base64String: string): Uint8Array {
  if (typeof base64String !== 'string') {
    throw new TypeError('Input must be a string');
  }
  const binString = atob(base64String);
  const len = binString.length;
  const bytes = new Uint8Array(len);
  for (let i = 0; i < len; i++) {
    bytes[i] = binString.charCodeAt(i);
  }
  return bytes;
}
