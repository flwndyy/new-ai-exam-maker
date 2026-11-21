// In-memory session. The access token lives only in this module's memory; the refresh
// token is kept in sessionStorage so a page reload can restore the session. Neither
// token is ever written to localStorage, a URL or a log.

import { setAccessTokenProvider } from "@/lib/api/auth-token";
import { refreshTokens, TokenEndpointError, type TokenSet } from "./cognito";
import { getAuthConfig } from "./config";

export type SessionStatus = "unknown" | "signedIn" | "signedOut" | "denied";

export const REFRESH_MARGIN_MS = 300_000;
const REFRESH_KEY = "exammaker.auth.refresh";

let status: SessionStatus = "unknown";
let accessToken: string | null = null;
let expiresAt = 0;
let refreshInFlight: Promise<string | null> | null = null;
// Bumped on every clearSession so a refresh that settles afterwards cannot resurrect it.
let generation = 0;
let listeners = new Set<(s: SessionStatus) => void>();

function readRefreshToken(): string | null {
  try {
    return sessionStorage.getItem(REFRESH_KEY);
  } catch {
    return null;
  }
}

function writeRefreshToken(token: string | null): void {
  try {
    if (token === null) sessionStorage.removeItem(REFRESH_KEY);
    else sessionStorage.setItem(REFRESH_KEY, token);
  } catch {
    // Storage unavailable: the session simply cannot survive a reload.
  }
}

function setStatus(next: SessionStatus): void {
  if (status === next) return;
  status = next;
  for (const listener of [...listeners]) listener(next);
}

export function getStatus(): SessionStatus {
  return status;
}

export function getRefreshToken(): string | null {
  return readRefreshToken();
}

export function subscribeSession(listener: (s: SessionStatus) => void): () => void {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

export function setTokens(t: TokenSet): void {
  accessToken = t.accessToken;
  expiresAt = Date.now() + t.expiresIn * 1000;
  // Cognito rotates refresh tokens only when it returns a new one; otherwise keep the old.
  if (t.refreshToken) writeRefreshToken(t.refreshToken);
  setStatus("signedIn");
}

export function clearSession(next: "signedOut" | "denied"): void {
  generation += 1;
  accessToken = null;
  expiresAt = 0;
  writeRefreshToken(null);
  setStatus(next);
}

function isDefinitiveRejection(error: unknown): boolean {
  return (
    error instanceof TokenEndpointError &&
    error.status >= 400 &&
    error.status < 500 &&
    error.status !== 429
  );
}

async function runRefresh(): Promise<string | null> {
  const cfg = getAuthConfig();
  const refreshToken = readRefreshToken();
  if (!cfg) return null;
  if (!refreshToken) {
    // Signed in without a refresh token (e.g. site data cleared): no later 401 could be
    // recovered, so end the session and let the guard start a new login.
    if (status === "signedIn") clearSession("signedOut");
    return null;
  }

  const startedAt = generation;
  try {
    const tokens = await refreshTokens(cfg, refreshToken);
    if (generation !== startedAt) return null;
    setTokens(tokens);
    return tokens.accessToken;
  } catch (error) {
    // Only a definitive rejection (4xx other than 429) ends the session. Network errors,
    // 429 and 5xx are transient: they propagate so callers can retry without losing
    // the refresh token.
    if (isDefinitiveRejection(error)) {
      if (generation === startedAt) clearSession("signedOut");
      return null;
    }
    throw error;
  }
}

/** Single-flight: concurrent callers share one token request until it settles. */
export function forceRefresh(): Promise<string | null> {
  if (!refreshInFlight) {
    const inFlight = runRefresh().finally(() => {
      if (refreshInFlight === inFlight) refreshInFlight = null;
    });
    refreshInFlight = inFlight;
  }
  return refreshInFlight;
}

export async function getValidAccessToken(): Promise<string | null> {
  if (accessToken && expiresAt - Date.now() > REFRESH_MARGIN_MS) return accessToken;
  const cached = accessToken;
  try {
    return await forceRefresh();
  } catch (error) {
    // Transient failure during a proactive refresh: the cached token is still usable
    // until it actually expires.
    if (cached && accessToken === cached && Date.now() < expiresAt) return cached;
    throw error;
  }
}

export async function restoreSession(): Promise<SessionStatus> {
  if (!readRefreshToken()) {
    clearSession("signedOut");
    return status;
  }
  await forceRefresh();
  return status;
}

export function installAccessTokenProvider(): void {
  setAccessTokenProvider(getValidAccessToken);
}

export function __resetSessionForTests(): void {
  status = "unknown";
  accessToken = null;
  expiresAt = 0;
  refreshInFlight = null;
  generation = 0;
  listeners = new Set();
  writeRefreshToken(null);
}
