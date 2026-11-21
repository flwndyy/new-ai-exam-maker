"use client";

import { useQueryClient } from "@tanstack/react-query";
import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useRef,
  useState,
  type ReactNode,
} from "react";

import { api } from "@/lib/api/client";
import type { components } from "@/lib/api/schema";
import { buildLogoutUrl, revokeToken } from "./cognito";
import { getAuthConfig } from "./config";
import { startLogin } from "./login";
import {
  clearSession,
  getRefreshToken,
  getStatus,
  installAccessTokenProvider,
  restoreSession,
  subscribeSession,
  type SessionStatus,
} from "./session";

type Me = components["schemas"]["Me"];
type Role = components["schemas"]["Role"];

export type AuthStatus =
  | "loading"
  | "signedOut"
  | "signedIn"
  | "denied"
  | "misconfigured"
  | "error"
  | "loggingOut";

export interface AuthContextValue {
  status: AuthStatus;
  me: Me | null;
  login(returnTo?: string): Promise<void>;
  logout(): Promise<void>;
  retry(): void;
  /** UX only (show/hide UI). The API enforces authorization from the DB user. */
  hasRole(...roles: Role[]): boolean;
}

const AuthContext = createContext<AuthContextValue | null>(null);

const PUBLIC_PATHS = ["/auth/callback"];
const REVOKE_TIMEOUT_MS = 5000;

/** Paths reachable without a session. Accepts both `/x` and `/x/` (trailingSlash export). */
export function isPublicPath(pathname: string): boolean {
  const normalized = pathname.length > 1 ? pathname.replace(/\/+$/, "") : pathname;
  return PUBLIC_PATHS.includes(normalized);
}

export function AuthProvider({ children }: { children: ReactNode }) {
  const queryClient = useQueryClient();
  // NEXT_PUBLIC_* config is inlined at build time, so this is identical on server and client.
  const [status, setStatusState] = useState<AuthStatus>(() =>
    getAuthConfig() ? "loading" : "misconfigured",
  );
  const [me, setMe] = useState<Me | null>(null);
  // Mirror of `status` readable from async callbacks and session events.
  const statusRef = useRef<AuthStatus>(status);
  // Only the latest /me request may apply its result.
  const meRequestRef = useRef(0);
  const meRequestedRef = useRef(false);

  const invalidateMe = useCallback(() => {
    meRequestRef.current++;
  }, []);

  const applyStatus = useCallback((next: AuthStatus) => {
    statusRef.current = next;
    setStatusState(next);
  }, []);

  const loadMe = useCallback(async () => {
    const request = ++meRequestRef.current;
    meRequestedRef.current = true;
    try {
      const { data, response } = await api.GET("/me");
      if (request !== meRequestRef.current || statusRef.current === "loggingOut") return;
      if (data) {
        setMe(data);
        applyStatus("signedIn");
      } else if (response.status !== 401) {
        applyStatus("error");
      } else if (getStatus() === "signedIn" && statusRef.current === "loading") {
        // The middleware returned the original 401 without touching the session
        // (transient refresh failure): no event will come, so offer a retry.
        applyStatus("error");
      }
      // Otherwise the 401 is reflected through the session event (denied/signedOut).
    } catch {
      if (request !== meRequestRef.current || statusRef.current === "loggingOut") return;
      applyStatus("error");
    }
  }, [applyStatus]);

  const bootstrap = useCallback(async () => {
    meRequestedRef.current = false;
    applyStatus("loading");
    try {
      const result = await restoreSession();
      // The signedIn session event normally started the /me request already.
      if (result === "signedIn") {
        if (!meRequestedRef.current) await loadMe();
      } else if (
        (result === "signedOut" || result === "denied") &&
        statusRef.current === "loading"
      ) {
        // The session module de-duplicates events: when it was already in this state
        // (e.g. StrictMode's second effect run) no event fires, so act on the result.
        invalidateMe();
        setMe(null);
        applyStatus(result);
      }
    } catch {
      if (statusRef.current === "loading") applyStatus("error");
    }
  }, [applyStatus, invalidateMe, loadMe]);

  useEffect(() => {
    if (statusRef.current === "misconfigured") return;
    installAccessTokenProvider();

    const unsubscribe = subscribeSession((next: SessionStatus) => {
      // Ignore session events during logout so AuthGate never starts a new login.
      if (statusRef.current === "loggingOut") return;
      if (next === "signedIn") {
        if (statusRef.current !== "signedIn") applyStatus("loading");
        void loadMe();
      } else if (next === "signedOut" || next === "denied") {
        invalidateMe();
        setMe(null);
        applyStatus(next);
      }
    });
    void bootstrap();

    return () => {
      unsubscribe();
      invalidateMe();
    };
  }, [applyStatus, bootstrap, invalidateMe, loadMe]);

  useEffect(() => {
    // A page restored from the back/forward cache keeps its frozen React state (e.g.
    // "loggingOut", or "signedOut" with the login redirect already started) and would
    // spin for ever. Reloading rebuilds the session from sessionStorage.
    const onPageShow = (event: PageTransitionEvent) => {
      if (event.persisted) window.location.reload();
    };
    window.addEventListener("pageshow", onPageShow);
    return () => window.removeEventListener("pageshow", onPageShow);
  }, []);

  const login = useCallback((returnTo: string = "/") => startLogin(returnTo), []);

  const logout = useCallback(async () => {
    const cfg = getAuthConfig();
    // Set first: while loggingOut the provider ignores session events.
    applyStatus("loggingOut");
    invalidateMe();
    const refreshToken = getRefreshToken();
    // Clear locally before the network call, so a reload during a slow revoke cannot
    // restore the session.
    clearSession("signedOut");
    queryClient.clear();
    setMe(null);
    if (cfg && refreshToken) {
      try {
        await revokeToken(cfg, refreshToken, AbortSignal.timeout(REVOKE_TIMEOUT_MS));
      } catch {
        // Best effort (network error, rejection or timeout): logout continues regardless.
      }
    }
    if (cfg) location.assign(buildLogoutUrl(cfg));
  }, [applyStatus, invalidateMe, queryClient]);

  const retry = useCallback(() => {
    if (statusRef.current === "misconfigured" || statusRef.current === "loggingOut") return;
    if (getStatus() === "signedIn") {
      applyStatus("loading");
      void loadMe();
    } else {
      void bootstrap();
    }
  }, [applyStatus, bootstrap, loadMe]);

  const hasRole = useCallback(
    (...roles: Role[]) => me !== null && roles.includes(me.role),
    [me],
  );

  const value = useMemo<AuthContextValue>(
    () => ({ status, me, login, logout, retry, hasRole }),
    [status, me, login, logout, retry, hasRole],
  );

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthContextValue {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error("useAuth must be used within AuthProvider");
  return ctx;
}
