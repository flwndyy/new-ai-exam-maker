import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { LoginCallbackError, completeLogin, startLogin } from "./login";

const exchangeCodeMock = vi.hoisted(() => vi.fn());
const setTokensMock = vi.hoisted(() => vi.fn());
const getAuthConfigMock = vi.hoisted(() => vi.fn());

vi.mock("./cognito", async (importOriginal) => {
  const actual = await importOriginal<typeof import("./cognito")>();
  return { ...actual, exchangeCode: exchangeCodeMock };
});

vi.mock("./session", () => ({
  setTokens: setTokensMock,
}));

vi.mock("./config", async (importOriginal) => {
  const actual = await importOriginal<typeof import("./config")>();
  return { ...actual, getAuthConfig: getAuthConfigMock };
});

const PENDING_KEY = "exammaker.auth.pending";
const cfg = { domain: "https://example.auth.amazoncognito.com", clientId: "client" };

function storePending(overrides: Partial<{ state: string; verifier: string; returnTo: string }> = {}) {
  sessionStorage.setItem(
    PENDING_KEY,
    JSON.stringify({ state: "s1", verifier: "v1", returnTo: "/exams/", ...overrides }),
  );
}

describe("startLogin", () => {
  const assign = vi.fn();
  const replace = vi.fn();

  beforeEach(() => {
    sessionStorage.clear();
    assign.mockReset();
    replace.mockReset();
    getAuthConfigMock.mockReset();
    getAuthConfigMock.mockReturnValue(cfg);
    vi.stubGlobal("location", {
      ...window.location,
      origin: "http://localhost:3000",
      assign,
      replace,
    });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it("stores pending data and redirects to the authorize URL carrying the stored state", async () => {
    await startLogin("/exams/");

    const pending = JSON.parse(sessionStorage.getItem(PENDING_KEY) as string) as {
      state: string;
      verifier: string;
      returnTo: string;
    };
    expect(pending.returnTo).toBe("/exams/");
    expect(pending.state).toBeTruthy();
    expect(pending.verifier).toBeTruthy();

    // replace, not assign: the page left behind must not be a Back target (bfcache).
    expect(assign).not.toHaveBeenCalled();
    expect(replace).toHaveBeenCalledTimes(1);
    const url = new URL(replace.mock.calls[0][0] as string);
    expect(url.origin).toBe(cfg.domain);
    expect(url.pathname).toBe("/oauth2/authorize");
    expect(url.searchParams.get("state")).toBe(pending.state);
    expect(url.searchParams.get("code_challenge")).toBeTruthy();
    expect(url.searchParams.get("code_challenge")).not.toBe(pending.verifier);
  });

  it("throws when auth is not configured and does not redirect", async () => {
    getAuthConfigMock.mockReturnValue(null);

    await expect(startLogin("/")).rejects.toThrow();
    expect(assign).not.toHaveBeenCalled();
    expect(replace).not.toHaveBeenCalled();
    expect(sessionStorage.getItem(PENDING_KEY)).toBeNull();
  });
});

describe("completeLogin", () => {
  beforeEach(() => {
    sessionStorage.clear();
    exchangeCodeMock.mockReset();
    setTokensMock.mockReset();
    getAuthConfigMock.mockReset();
    getAuthConfigMock.mockReturnValue(cfg);
  });

  async function codeOf(search: string): Promise<string> {
    try {
      await completeLogin(new URLSearchParams(search));
    } catch (error) {
      expect(error).toBeInstanceOf(LoginCallbackError);
      return (error as LoginCallbackError).code;
    }
    return "no_error";
  }

  it("rejects a mismatching state", async () => {
    storePending();
    expect(await codeOf("code=c1&state=other")).toBe("state_mismatch");
    expect(sessionStorage.getItem(PENDING_KEY)).toBeNull();
    expect(exchangeCodeMock).not.toHaveBeenCalled();
  });

  it("rejects when there is no pending data", async () => {
    expect(await codeOf("code=c1&state=s1")).toBe("missing_pending");
    expect(exchangeCodeMock).not.toHaveBeenCalled();
  });

  it("rejects corrupt pending data as missing_pending and removes it", async () => {
    sessionStorage.setItem(PENDING_KEY, "not-json");
    expect(await codeOf("code=c1&state=s1")).toBe("missing_pending");
    expect(sessionStorage.getItem(PENDING_KEY)).toBeNull();
  });

  it("maps an error parameter to cognito_error and removes pending", async () => {
    storePending();
    expect(await codeOf("error=access_denied&state=s1")).toBe("cognito_error");
    expect(sessionStorage.getItem(PENDING_KEY)).toBeNull();
  });

  it("reports cognito_error even when there is no pending data", async () => {
    expect(await codeOf("error=access_denied")).toBe("cognito_error");
  });

  it("rejects a missing code and removes pending", async () => {
    storePending();
    expect(await codeOf("state=s1")).toBe("missing_code");
    expect(sessionStorage.getItem(PENDING_KEY)).toBeNull();
  });

  it("maps a token endpoint failure to token_exchange_failed", async () => {
    storePending();
    exchangeCodeMock.mockRejectedValue(
      Object.assign(new Error("rejected"), { code: "invalid_grant", status: 400 }),
    );
    expect(await codeOf("code=c1&state=s1")).toBe("token_exchange_failed");
    expect(sessionStorage.getItem(PENDING_KEY)).toBeNull();
    expect(setTokensMock).not.toHaveBeenCalled();
  });

  it("maps a network failure to token_exchange_failed", async () => {
    storePending();
    exchangeCodeMock.mockRejectedValue(new TypeError("Failed to fetch"));
    expect(await codeOf("code=c1&state=s1")).toBe("token_exchange_failed");
    expect(sessionStorage.getItem(PENDING_KEY)).toBeNull();
  });

  it("exchanges the code with the stored verifier, sets tokens once, returns sanitized returnTo", async () => {
    storePending({ returnTo: "/exams/" });
    const tokens = { accessToken: "a1", expiresIn: 3600, refreshToken: "r1" };
    exchangeCodeMock.mockResolvedValue(tokens);

    const result = await completeLogin(new URLSearchParams("code=c1&state=s1"));

    expect(exchangeCodeMock).toHaveBeenCalledWith(cfg, { code: "c1", codeVerifier: "v1" });
    expect(setTokensMock).toHaveBeenCalledTimes(1);
    expect(setTokensMock).toHaveBeenCalledWith(tokens);
    expect(result).toEqual({ returnTo: "/exams/" });
    expect(sessionStorage.getItem(PENDING_KEY)).toBeNull();
  });

  it("sanitizes an unsafe stored returnTo", async () => {
    storePending({ returnTo: "https://evil.example/" });
    exchangeCodeMock.mockResolvedValue({ accessToken: "a1", expiresIn: 3600 });

    const result = await completeLogin(new URLSearchParams("code=c1&state=s1"));

    expect(result.returnTo).toBe("/");
  });

  async function errorOf(search: string): Promise<LoginCallbackError> {
    const error = await completeLogin(new URLSearchParams(search)).catch((e: unknown) => e);
    expect(error).toBeInstanceOf(LoginCallbackError);
    return error as LoginCallbackError;
  }

  it("carries the sanitized returnTo on errors raised after the pending data was read", async () => {
    storePending({ returnTo: "/exams/view/?id=1" });
    expect((await errorOf("code=c1&state=other")).returnTo).toBe("/exams/view/?id=1");

    storePending({ returnTo: "/exams/" });
    expect((await errorOf("error=access_denied&state=s1")).returnTo).toBe("/exams/");

    storePending({ returnTo: "/exams/" });
    expect((await errorOf("state=s1")).returnTo).toBe("/exams/");

    storePending({ returnTo: "/exams/" });
    exchangeCodeMock.mockRejectedValue(new TypeError("Failed to fetch"));
    expect((await errorOf("code=c1&state=s1")).returnTo).toBe("/exams/");

    storePending({ returnTo: "https://evil.example/" });
    expect((await errorOf("code=c1&state=other")).returnTo).toBe("/");
  });

  it("carries no returnTo when there was no pending data", async () => {
    expect((await errorOf("code=c1&state=s1")).returnTo).toBeUndefined();
    expect((await errorOf("error=access_denied")).returnTo).toBeUndefined();
  });

  it("fails with token_exchange_failed when auth is not configured", async () => {
    storePending();
    getAuthConfigMock.mockReturnValue(null);

    expect(await codeOf("code=c1&state=s1")).toBe("token_exchange_failed");
    expect(exchangeCodeMock).not.toHaveBeenCalled();
  });
});
