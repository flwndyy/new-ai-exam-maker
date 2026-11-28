import { describe, expect, it } from "vitest";
import {
  base64UrlEncode,
  createCodeChallenge,
  createPkcePair,
  createState,
  randomUrlSafe,
} from "./pkce";

describe("pkce", () => {
  it("matches RFC 7636 appendix B vector", async () => {
    expect(await createCodeChallenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk")).toBe(
      "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
    );
  });

  it("creates a 43-char url-safe verifier and distinct states", async () => {
    const { verifier, challenge } = await createPkcePair();
    expect(verifier).toMatch(/^[A-Za-z0-9_-]{43}$/);
    expect(challenge).toBe(await createCodeChallenge(verifier));
    expect(createState()).not.toBe(createState());
  });

  it("encodes base64url without padding or unsafe characters", () => {
    expect(base64UrlEncode(new Uint8Array([251, 255, 254]))).toBe("-__-");
    expect(base64UrlEncode(new Uint8Array([1]))).toBe("AQ");
    expect(randomUrlSafe(16)).toMatch(/^[A-Za-z0-9_-]{22}$/);
  });
});
