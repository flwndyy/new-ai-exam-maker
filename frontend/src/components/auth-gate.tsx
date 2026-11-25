"use client";

import { usePathname } from "next/navigation";
import { useCallback, useEffect, useRef, useState, type ReactNode } from "react";

import { Button } from "@/components/ui/button";
import { EmptyState } from "@/components/ui/empty-state";
import { isPublicPath, useAuth } from "@/lib/auth/auth-context";
import { vi } from "@/messages/vi";

function Centered({ children }: { children: ReactNode }) {
  return <div className="mx-auto max-w-lg px-4 py-12">{children}</div>;
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
          <Centered>
            <EmptyState headingLevel={1}
              title={vi.auth.callbackFailed}
              action={
                <Button
                  variant="secondary"
                  onClick={() => {
                    setLoginFailed(false);
                    beginLogin();
                  }}
                >
                  {vi.auth.retry}
                </Button>
              }
            />
          </Centered>
        );
      }
      return (
        <Centered>
          <EmptyState headingLevel={1} busy title={vi.auth.redirecting} />
        </Centered>
      );
    case "denied":
      return (
        <Centered>
          <EmptyState headingLevel={1}
            title={vi.auth.deniedTitle}
            description={vi.auth.deniedBody}
            action={
              <Button variant="secondary" onClick={() => void logout()}>
                {vi.auth.logout}
              </Button>
            }
          />
        </Centered>
      );
    case "misconfigured":
      return (
        <Centered>
          <EmptyState headingLevel={1} title={vi.auth.misconfigured} />
        </Centered>
      );
    case "error":
      return (
        <Centered>
          <EmptyState headingLevel={1}
            title={vi.auth.networkError}
            action={
              <Button variant="secondary" onClick={retry}>
                {vi.auth.retry}
              </Button>
            }
          />
        </Centered>
      );
    case "loading":
    case "loggingOut":
      return (
        <Centered>
          <EmptyState headingLevel={1} busy title={vi.common.loading} />
        </Centered>
      );
  }
}
