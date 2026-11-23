// PKCE (RFC 7636) and state helpers built on the Web Crypto API.

const VERIFIER_BYTES = 32;
const STATE_BYTES = 16;

export function base64UrlEncode(bytes: Uint8Array): string {
  let binary = "";
  for (const byte of bytes) {
    binary += String.fromCharCode(byte);
  }
  return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

export function randomUrlSafe(byteLength: number): string {
  const bytes = new Uint8Array(byteLength);
  crypto.getRandomValues(bytes);
  return base64UrlEncode(bytes);
}

/** S256 code challenge: BASE64URL(SHA-256(ASCII(verifier))). */
export async function createCodeChallenge(verifier: string): Promise<string> {
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(verifier));
  return base64UrlEncode(new Uint8Array(digest));
}

export async function createPkcePair(): Promise<{ verifier: string; challenge: string }> {
  const verifier = randomUrlSafe(VERIFIER_BYTES);
  return { verifier, challenge: await createCodeChallenge(verifier) };
}

export function createState(): string {
  return randomUrlSafe(STATE_BYTES);
}
