"use client";

import { usePathname } from "next/navigation";
import { useCallback, useEffect, useRef, useState, type ReactNode } from "react";

import { isPublicPath, useAuth } from "@/lib/auth/auth-context";
import { vi } from "@/messages/vi";

function Notice({ children }: { children: ReactNode }) {
  return <div className="py-12 text-center text-slate-600">{children}</div>;
}

/**
 * UX only: decides what to render for the current session. It is not a security
 * boundary; the API enforces authentication and authorization on every request.
 */
export function AuthGate({ children }: { children: ReactNode }) {
  const { status, login, logout, retry } = useAuth();
  const pathname = usePathname();
  const isPublic = isPublicPath(pathname);
  const [loginFailed, setLoginFailed] = useState(false);
  const loginStarted = useRef(false);

  const beginLogin = useCallback(() => {
    // Read the location here: useSearchParams would force a Suspense boundary around
    // the whole layout in a static export.
    const returnTo = window.location.pathname + window.location.search + window.location.hash;
    login(returnTo).catch(() => setLoginFailed(true));
  }, [login]);

  useEffect(() => {
    if (status !== "signedOut" || isPublic) {
      loginStarted.current = false;
      return;
    }
    if (loginStarted.current) return;
    loginStarted.current = true;
    beginLogin();
  }, [status, isPublic, beginLogin]);

  if (isPublic) return <>{children}</>;

  switch (status) {
    case "signedIn":
      return <>{children}</>;
    case "signedOut":
      if (loginFailed) {
        return (
          <Notice>
            <p>{vi.auth.callbackFailed}</p>
            <button
              type="button"
              className="mt-4 rounded border border-slate-300 px-3 py-1 hover:bg-slate-100"
              onClick={() => {
                setLoginFailed(false);
                beginLogin();
              }}
            >
              {vi.auth.retry}
            </button>
          </Notice>
        );
      }
      return <Notice>{vi.auth.redirecting}</Notice>;
    case "denied":
      return (
        <Notice>
          <h1 className="text-lg font-semibold text-slate-900">{vi.auth.deniedTitle}</h1>
          <p className="mt-2">{vi.auth.deniedBody}</p>
          <button
            type="button"
            className="mt-4 rounded border border-slate-300 px-3 py-1 hover:bg-slate-100"
            onClick={() => void logout()}
          >
            {vi.auth.logout}
          </button>
        </Notice>
      );
    case "misconfigured":
      return <Notice>{vi.auth.misconfigured}</Notice>;
    case "error":
      return (
        <Notice>
          <p>{vi.auth.networkError}</p>
          <button
            type="button"
            className="mt-4 rounded border border-slate-300 px-3 py-1 hover:bg-slate-100"
            onClick={retry}
          >
            {vi.auth.retry}
          </button>
        </Notice>
      );
    case "loading":
    case "loggingOut":
      return <Notice>{vi.common.loading}</Notice>;
  }
}
