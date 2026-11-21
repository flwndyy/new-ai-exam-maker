import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { TokenEndpointError } from "./cognito";
import {
  __resetSessionForTests,
  clearSession,
  forceRefresh,
  getRefreshToken,
  getStatus,
  getValidAccessToken,
  installAccessTokenProvider,
  restoreSession,
  type SessionStatus,
  setTokens,
  subscribeSession,
} from "./session";

const refreshTokensMock = vi.hoisted(() => vi.fn());
const getAuthConfigMock = vi.hoisted(() => vi.fn());

vi.mock("./cognito", async (importOriginal) => {
  const actual = await importOriginal<typeof import("./cognito")>();
  return { ...actual, refreshTokens: refreshTokensMock };
});

vi.mock("./config", () => ({
  getAuthConfig: getAuthConfigMock,
}));

const setAccessTokenProviderMock = vi.hoisted(() => vi.fn());
vi.mock("@/lib/api/auth-token", () => ({
  setAccessTokenProvider: setAccessTokenProviderMock,
}));

const REFRESH_KEY = "exammaker.auth.refresh";
const cfg = { domain: "https://example.auth.amazoncognito.com", clientId: "client" };
const MINUTE = 60;

describe("session", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date("2026-01-01T00:00:00Z"));
    __resetSessionForTests();
    refreshTokensMock.mockReset();
    setAccessTokenProviderMock.mockReset();
    getAuthConfigMock.mockReset();
    getAuthConfigMock.mockReturnValue(cfg);
    localStorage.clear();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  describe("setTokens", () => {
    it("keeps the refresh token in sessionStorage only and the access token nowhere", () => {
      setTokens({ accessToken: "access-secret", expiresIn: 3600, refreshToken: "r1" });

      expect(sessionStorage.getItem(REFRESH_KEY)).toBe("r1");
      expect(localStorage.length).toBe(0);
      for (const storage of [localStorage, sessionStorage]) {
        for (let i = 0; i < storage.length; i++) {
          const value = storage.getItem(storage.key(i) as string);
          expect(value).not.toContain("access-secret");
        }
      }
      expect(getStatus()).toBe("signedIn");
      expect(getRefreshToken()).toBe("r1");
    });

    it("keeps the previous refresh token when none is returned", () => {
      setTokens({ accessToken: "a1", expiresIn: 3600, refreshToken: "r1" });
      setTokens({ accessToken: "a2", expiresIn: 3600 });
      expect(getRefreshToken()).toBe("r1");
    });
  });

  describe("getValidAccessToken", () => {
    it("returns the cached token without refreshing when 10 minutes remain", async () => {
      setTokens({ accessToken: "a1", expiresIn: 10 * MINUTE, refreshToken: "r1" });

      await expect(getValidAccessToken()).resolves.toBe("a1");
      expect(refreshTokensMock).not.toHaveBeenCalled();
    });

    it("refreshes once when 4 minutes remain", async () => {
      setTokens({ accessToken: "a1", expiresIn: 4 * MINUTE, refreshToken: "r1" });
      refreshTokensMock.mockResolvedValue({ accessToken: "a2", expiresIn: 3600 });

      await expect(getValidAccessToken()).resolves.toBe("a2");
      expect(refreshTokensMock).toHaveBeenCalledTimes(1);
      expect(refreshTokensMock).toHaveBeenCalledWith(cfg, "r1");
    });

    it("refreshes once the clock moves into the margin", async () => {
      setTokens({ accessToken: "a1", expiresIn: 10 * MINUTE, refreshToken: "r1" });
      refreshTokensMock.mockResolvedValue({ accessToken: "a2", expiresIn: 3600 });

      vi.setSystemTime(Date.now() + 6 * MINUTE * 1000);

      await expect(getValidAccessToken()).resolves.toBe("a2");
      expect(refreshTokensMock).toHaveBeenCalledTimes(1);
    });

    it("falls back to the cached token when a proactive refresh fails transiently", async () => {
      setTokens({ accessToken: "a1", expiresIn: 4 * MINUTE, refreshToken: "r1" });
      refreshTokensMock.mockRejectedValue(new TypeError("Failed to fetch"));

      await expect(getValidAccessToken()).resolves.toBe("a1");
      expect(refreshTokensMock).toHaveBeenCalledTimes(1);
      expect(getStatus()).toBe("signedIn");
      expect(sessionStorage.getItem(REFRESH_KEY)).toBe("r1");
    });

    it("rejects when the refresh fails transiently and the cached token has expired", async () => {
      setTokens({ accessToken: "a1", expiresIn: 4 * MINUTE, refreshToken: "r1" });
      refreshTokensMock.mockRejectedValue(new TypeError("Failed to fetch"));
      vi.setSystemTime(Date.now() + 5 * MINUTE * 1000);

      await expect(getValidAccessToken()).rejects.toThrow("Failed to fetch");
      expect(getStatus()).toBe("signedIn");
    });

    it("does not fall back to the cached token after a definitive rejection", async () => {
      setTokens({ accessToken: "a1", expiresIn: 4 * MINUTE, refreshToken: "r1" });
      refreshTokensMock.mockRejectedValue(new TokenEndpointError("invalid_grant", 400));

      await expect(getValidAccessToken()).resolves.toBeNull();
      expect(getStatus()).toBe("signedOut");
    });

    it("returns null with no network call when there is nothing to refresh", async () => {
      await expect(getValidAccessToken()).resolves.toBeNull();
      expect(refreshTokensMock).not.toHaveBeenCalled();
    });
  });

  describe("forceRefresh", () => {
    it("is single-flight: three parallel calls hit the endpoint once", async () => {
      setTokens({ accessToken: "a1", expiresIn: 3600, refreshToken: "r1" });
      refreshTokensMock.mockResolvedValue({ accessToken: "a2", expiresIn: 3600 });

      const results = await Promise.all([forceRefresh(), forceRefresh(), forceRefresh()]);

      expect(refreshTokensMock).toHaveBeenCalledTimes(1);
      expect(results).toEqual(["a2", "a2", "a2"]);
    });

    it("starts a new refresh after the previous one settled", async () => {
      setTokens({ accessToken: "a1", expiresIn: 3600, refreshToken: "r1" });
      refreshTokensMock
        .mockResolvedValueOnce({ accessToken: "a2", expiresIn: 3600 })
        .mockResolvedValueOnce({ accessToken: "a3", expiresIn: 3600 });

      await expect(forceRefresh()).resolves.toBe("a2");
      await expect(forceRefresh()).resolves.toBe("a3");
      expect(refreshTokensMock).toHaveBeenCalledTimes(2);
    });

    it("returns null without a network call when there is no refresh token", async () => {
      await expect(forceRefresh()).resolves.toBeNull();
      expect(refreshTokensMock).not.toHaveBeenCalled();
    });

    it("signs out when signed in but the refresh token has disappeared", async () => {
      setTokens({ accessToken: "a1", expiresIn: 3600, refreshToken: "r1" });
      const events: SessionStatus[] = [];
      subscribeSession((s) => events.push(s));
      // e.g. the user cleared site data: a later 401 could never be recovered.
      sessionStorage.removeItem(REFRESH_KEY);

      await expect(forceRefresh()).resolves.toBeNull();
      expect(refreshTokensMock).not.toHaveBeenCalled();
      expect(getStatus()).toBe("signedOut");
      expect(events).toEqual(["signedOut"]);
      await expect(getValidAccessToken()).resolves.toBeNull();
    });

    it("returns null without a network call when auth is not configured", async () => {
      setTokens({ accessToken: "a1", expiresIn: 3600, refreshToken: "r1" });
      getAuthConfigMock.mockReturnValue(null);

      await expect(forceRefresh()).resolves.toBeNull();
      expect(refreshTokensMock).not.toHaveBeenCalled();
    });

    it("signs out and returns null when the token endpoint rejects the refresh token", async () => {
      setTokens({ accessToken: "a1", expiresIn: 3600, refreshToken: "r1" });
      refreshTokensMock.mockRejectedValue(new TokenEndpointError("invalid_grant", 400));

      await expect(forceRefresh()).resolves.toBeNull();
      expect(getStatus()).toBe("signedOut");
      expect(sessionStorage.getItem(REFRESH_KEY)).toBeNull();
      await expect(getValidAccessToken()).resolves.toBeNull();
    });

    it.each([503, 500, 502, 429])(
      "rethrows a transient %i from the token endpoint and keeps the session",
      async (status) => {
        setTokens({ accessToken: "a1", expiresIn: 3600, refreshToken: "r1" });
        refreshTokensMock.mockRejectedValue(new TokenEndpointError("unknown_error", status));

        await expect(forceRefresh()).rejects.toBeInstanceOf(TokenEndpointError);
        expect(getStatus()).toBe("signedIn");
        expect(sessionStorage.getItem(REFRESH_KEY)).toBe("r1");
      },
    );

    it("rethrows network errors and keeps the session and refresh token", async () => {
      setTokens({ accessToken: "a1", expiresIn: 3600, refreshToken: "r1" });
      refreshTokensMock.mockRejectedValue(new TypeError("Failed to fetch"));

      await expect(forceRefresh()).rejects.toThrow("Failed to fetch");
      expect(getStatus()).toBe("signedIn");
      expect(sessionStorage.getItem(REFRESH_KEY)).toBe("r1");
    });

    it("allows a retry after a network failure", async () => {
      setTokens({ accessToken: "a1", expiresIn: 3600, refreshToken: "r1" });
      refreshTokensMock
        .mockRejectedValueOnce(new TypeError("Failed to fetch"))
        .mockResolvedValueOnce({ accessToken: "a2", expiresIn: 3600 });

      await expect(forceRefresh()).rejects.toThrow();
      await expect(forceRefresh()).resolves.toBe("a2");
    });

    it("stores the rotated refresh token", async () => {
      setTokens({ accessToken: "a1", expiresIn: 3600, refreshToken: "r1" });
      refreshTokensMock.mockResolvedValue({
        accessToken: "a2",
        expiresIn: 3600,
        refreshToken: "r2",
      });

      await forceRefresh();

      expect(sessionStorage.getItem(REFRESH_KEY)).toBe("r2");
    });

    it("does not resurrect a session cleared while the refresh was in flight", async () => {
      setTokens({ accessToken: "a1", expiresIn: 3600, refreshToken: "r1" });
      let resolveRefresh: (t: { accessToken: string; expiresIn: number }) => void = () => {};
      refreshTokensMock.mockReturnValue(
        new Promise((resolve) => {
          resolveRefresh = resolve;
        }),
      );

      const pending = forceRefresh();
      clearSession("signedOut");
      resolveRefresh({ accessToken: "a2", expiresIn: 3600 });

      await expect(pending).resolves.toBeNull();
      expect(getStatus()).toBe("signedOut");
      expect(sessionStorage.getItem(REFRESH_KEY)).toBeNull();
    });
  });

  describe("restoreSession", () => {
    it("becomes signedOut without network when there is no stored refresh token", async () => {
      await expect(restoreSession()).resolves.toBe("signedOut");
      expect(refreshTokensMock).not.toHaveBeenCalled();
    });

    it("becomes signedIn when the stored refresh token works", async () => {
      sessionStorage.setItem(REFRESH_KEY, "r1");
      refreshTokensMock.mockResolvedValue({ accessToken: "a1", expiresIn: 3600 });

      await expect(restoreSession()).resolves.toBe("signedIn");
      expect(refreshTokensMock).toHaveBeenCalledWith(cfg, "r1");
    });

    it("becomes signedOut when the stored refresh token is rejected", async () => {
      sessionStorage.setItem(REFRESH_KEY, "r1");
      refreshTokensMock.mockRejectedValue(new TokenEndpointError("invalid_grant", 400));

      await expect(restoreSession()).resolves.toBe("signedOut");
      expect(sessionStorage.getItem(REFRESH_KEY)).toBeNull();
    });
  });

  describe("clearSession", () => {
    it("supports the denied status and removes the refresh token", () => {
      setTokens({ accessToken: "a1", expiresIn: 3600, refreshToken: "r1" });

      clearSession("denied");

      expect(getStatus()).toBe("denied");
      expect(getRefreshToken()).toBeNull();
      expect(sessionStorage.getItem(REFRESH_KEY)).toBeNull();
    });
  });

  describe("subscribeSession", () => {
    it("delivers status changes in order, only on actual change", () => {
      const seen: SessionStatus[] = [];
      subscribeSession((s) => seen.push(s));

      setTokens({ accessToken: "a1", expiresIn: 3600, refreshToken: "r1" });
      setTokens({ accessToken: "a2", expiresIn: 3600 });
      clearSession("signedOut");
      clearSession("signedOut");
      clearSession("denied");

      expect(seen).toEqual(["signedIn", "signedOut", "denied"]);
    });

    it("stops notifying after unsubscribe", () => {
      const listener = vi.fn();
      const unsubscribe = subscribeSession(listener);
      unsubscribe();

      setTokens({ accessToken: "a1", expiresIn: 3600 });

      expect(listener).not.toHaveBeenCalled();
    });
  });

  describe("installAccessTokenProvider", () => {
    it("registers getValidAccessToken as the API token provider", () => {
      installAccessTokenProvider();

      expect(setAccessTokenProviderMock).toHaveBeenCalledWith(getValidAccessToken);
    });
  });

  describe("__resetSessionForTests", () => {
    it("resets status, listeners and the refresh key", () => {
      const listener = vi.fn();
      subscribeSession(listener);
      setTokens({ accessToken: "a1", expiresIn: 3600, refreshToken: "r1" });
      listener.mockClear();

      __resetSessionForTests();

      expect(getStatus()).toBe("unknown");
      expect(sessionStorage.getItem(REFRESH_KEY)).toBeNull();
      setTokens({ accessToken: "a2", expiresIn: 3600 });
      expect(listener).not.toHaveBeenCalled();
    });
  });
});
