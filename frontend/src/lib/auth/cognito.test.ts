import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { getAuthConfig, logoutUri, redirectUri } from "./config";
import {
  buildAuthorizeUrl,
  buildLogoutUrl,
  exchangeCode,
  refreshTokens,
  revokeToken,
  TOKEN_REQUEST_TIMEOUT_MS,
  TokenEndpointError,
} from "./cognito";

const cfg = {
  domain: "https://example-prefix.auth.ap-southeast-1.amazoncognito.com",
  clientId: "example-client-id",
};

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

describe("getAuthConfig", () => {
  afterEach(() => {
    vi.unstubAllEnvs();
  });

  it("returns config and strips trailing slash", () => {
    vi.stubEnv("NEXT_PUBLIC_COGNITO_DOMAIN", "https://example.auth.amazoncognito.com/");
    vi.stubEnv("NEXT_PUBLIC_COGNITO_CLIENT_ID", "abc");
    expect(getAuthConfig()).toEqual({
      domain: "https://example.auth.amazoncognito.com",
      clientId: "abc",
    });
  });

  it("returns null when a variable is missing", () => {
    vi.stubEnv("NEXT_PUBLIC_COGNITO_DOMAIN", "");
    vi.stubEnv("NEXT_PUBLIC_COGNITO_CLIENT_ID", "abc");
    expect(getAuthConfig()).toBeNull();
    vi.stubEnv("NEXT_PUBLIC_COGNITO_DOMAIN", "https://example.auth.amazoncognito.com");
    vi.stubEnv("NEXT_PUBLIC_COGNITO_CLIENT_ID", "");
    expect(getAuthConfig()).toBeNull();
  });

  it("returns null when domain is not https", () => {
    vi.stubEnv("NEXT_PUBLIC_COGNITO_DOMAIN", "http://example.auth.amazoncognito.com");
    vi.stubEnv("NEXT_PUBLIC_COGNITO_CLIENT_ID", "abc");
    expect(getAuthConfig()).toBeNull();
  });

  it("derives redirect and logout uris from the origin", () => {
    expect(redirectUri()).toBe("http://localhost:3000/auth/callback/");
    expect(logoutUri()).toBe("http://localhost:3000/");
  });
});

describe("buildAuthorizeUrl", () => {
  it("includes all PKCE authorization code parameters", () => {
    const url = new URL(buildAuthorizeUrl(cfg, { state: "st", codeChallenge: "ch" }));
    expect(url.origin + url.pathname).toBe(`${cfg.domain}/oauth2/authorize`);
    expect(url.searchParams.get("response_type")).toBe("code");
    expect(url.searchParams.get("client_id")).toBe(cfg.clientId);
    expect(url.searchParams.get("redirect_uri")).toBe("http://localhost:3000/auth/callback/");
    expect(url.searchParams.get("scope")).toBe("openid email profile");
    expect(url.searchParams.get("state")).toBe("st");
    expect(url.searchParams.get("code_challenge")).toBe("ch");
    expect(url.searchParams.get("code_challenge_method")).toBe("S256");
    expect(url.searchParams.has("nonce")).toBe(false);
  });
});

describe("buildLogoutUrl", () => {
  it("builds the Cognito logout url", () => {
    expect(buildLogoutUrl(cfg)).toBe(
      `${cfg.domain}/logout?client_id=example-client-id&logout_uri=http%3A%2F%2Flocalhost%3A3000%2F`,
    );
  });
});

describe("token endpoint", () => {
  const fetchMock = vi.fn();

  beforeEach(() => {
    fetchMock.mockReset();
    vi.stubGlobal("fetch", fetchMock);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  function lastRequest(): { url: string; init: RequestInit; body: URLSearchParams } {
    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    return { url, init, body: new URLSearchParams(init.body as string) };
  }

  it("exchangeCode posts a form body and maps the token set", async () => {
    fetchMock.mockResolvedValue(
      jsonResponse({
        access_token: "at",
        id_token: "idt",
        refresh_token: "rt",
        expires_in: 3600,
        token_type: "Bearer",
      }),
    );
    const tokens = await exchangeCode(cfg, { code: "c1", codeVerifier: "v1" });
    expect(tokens).toEqual({ accessToken: "at", expiresIn: 3600, refreshToken: "rt" });
    expect(tokens).not.toHaveProperty("idToken");

    const { url, init, body } = lastRequest();
    expect(url).toBe(`${cfg.domain}/oauth2/token`);
    expect(init.method).toBe("POST");
    expect((init.headers as Record<string, string>)["Content-Type"]).toBe(
      "application/x-www-form-urlencoded",
    );
    expect(body.get("grant_type")).toBe("authorization_code");
    expect(body.get("client_id")).toBe(cfg.clientId);
    expect(body.get("code")).toBe("c1");
    expect(body.get("code_verifier")).toBe("v1");
    expect(body.get("redirect_uri")).toBe("http://localhost:3000/auth/callback/");
    expect(body.has("client_secret")).toBe(false);
  });

  it("maps invalid_grant to TokenEndpointError", async () => {
    fetchMock.mockResolvedValue(jsonResponse({ error: "invalid_grant" }, 400));
    const err = await exchangeCode(cfg, { code: "c", codeVerifier: "v" }).catch((e) => e);
    expect(err).toBeInstanceOf(TokenEndpointError);
    expect(err.code).toBe("invalid_grant");
    expect(err.status).toBe(400);
  });

  it("falls back to a generic code when the error body is not JSON", async () => {
    fetchMock.mockResolvedValue(new Response("oops", { status: 502 }));
    const err = await refreshTokens(cfg, "rt").catch((e) => e);
    expect(err).toBeInstanceOf(TokenEndpointError);
    expect(err.status).toBe(502);
    expect(err.code).toBe("unknown_error");
  });

  it("rethrows network errors unchanged", async () => {
    const networkError = new TypeError("Failed to fetch");
    fetchMock.mockRejectedValue(networkError);
    const err = await refreshTokens(cfg, "rt").catch((e) => e);
    expect(err).toBe(networkError);
    expect(err).not.toBeInstanceOf(TokenEndpointError);
  });

  it("refreshTokens sends refresh_token grant and leaves refreshToken undefined", async () => {
    fetchMock.mockResolvedValue(
      jsonResponse({ access_token: "at2", id_token: "idt", expires_in: 1800, token_type: "Bearer" }),
    );
    const tokens = await refreshTokens(cfg, "old-rt");
    expect(tokens).toEqual({ accessToken: "at2", expiresIn: 1800, refreshToken: undefined });
    expect(tokens.refreshToken).toBeUndefined();

    const { url, body } = lastRequest();
    expect(url).toBe(`${cfg.domain}/oauth2/token`);
    expect(body.get("grant_type")).toBe("refresh_token");
    expect(body.get("refresh_token")).toBe("old-rt");
    expect(body.get("client_id")).toBe(cfg.clientId);
    expect(body.has("client_secret")).toBe(false);
  });

  it("revokeToken posts token and client_id to /oauth2/revoke", async () => {
    fetchMock.mockResolvedValue(new Response(null, { status: 200 }));
    await revokeToken(cfg, "rt-to-revoke");
    const { url, init, body } = lastRequest();
    expect(url).toBe(`${cfg.domain}/oauth2/revoke`);
    expect(init.method).toBe("POST");
    expect(body.get("token")).toBe("rt-to-revoke");
    expect(body.get("client_id")).toBe(cfg.clientId);
  });

  it("revokeToken forwards an abort signal to fetch", async () => {
    fetchMock.mockResolvedValue(new Response(null, { status: 200 }));
    const controller = new AbortController();
    await revokeToken(cfg, "rt-to-revoke", controller.signal);
    expect(lastRequest().init.signal).toBe(controller.signal);
  });
});

describe("token endpoint timeout", () => {
  // Simulates a connection that never answers: it only settles when its signal aborts.
  const hangingFetch = vi.fn(
    (_url: string, init?: RequestInit) =>
      new Promise<Response>((_resolve, reject) => {
        init?.signal?.addEventListener("abort", () => reject(init.signal?.reason));
      }),
  );

  beforeEach(() => {
    vi.useFakeTimers();
    hangingFetch.mockClear();
    vi.stubGlobal("fetch", hangingFetch);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.useRealTimers();
  });

  it.each([
    ["exchangeCode", () => exchangeCode(cfg, { code: "c", codeVerifier: "v" })],
    ["refreshTokens", () => refreshTokens(cfg, "rt")],
  ])("%s rejects as a transient error once the request hangs past the timeout", async (_name, call) => {
    const settled = vi.fn();
    const pending = call().then(settled, (e: unknown) => {
      settled();
      return e;
    });

    await vi.advanceTimersByTimeAsync(TOKEN_REQUEST_TIMEOUT_MS - 1);
    expect(settled).not.toHaveBeenCalled();

    await vi.advanceTimersByTimeAsync(1);
    const err = await pending;
    // A DOMException from jsdom is not `instanceof Error` across realms; the name is the contract.
    expect(err).not.toBeInstanceOf(TokenEndpointError);
    expect((err as DOMException).name).toBe("TimeoutError");
  });

  it("leaves no timer behind when the request completes in time", async () => {
    hangingFetch.mockResolvedValueOnce(jsonResponse({ access_token: "at", expires_in: 3600 }));
    await refreshTokens(cfg, "rt");
    expect(vi.getTimerCount()).toBe(0);
  });
});
