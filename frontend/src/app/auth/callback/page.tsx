"use client";

import { useRouter, useSearchParams } from "next/navigation";
import { Suspense, useEffect, useRef, useState, type ReactNode } from "react";

import { Button } from "@/components/ui/button";
import { EmptyState } from "@/components/ui/empty-state";
import { completeLogin, LoginCallbackError, startLogin } from "@/lib/auth/login";
import { vi } from "@/messages/vi";

const CALLBACK_PATH = "/auth/callback/";

function Centered({ children }: { children: ReactNode }) {
  return <div className="mx-auto max-w-lg px-4 py-12">{children}</div>;
}

function CallbackContent() {
  const router = useRouter();
  const searchParams = useSearchParams();
  // Set on failure: where the retry button sends the user after logging in again.
  const [retryTo, setRetryTo] = useState<string | null>(null);
  // StrictMode runs effects twice in development; the code and the pending PKCE data
  // are single-use, so the exchange must run exactly once.
  const started = useRef(false);

  useEffect(() => {
    if (started.current) return;
    started.current = true;
    const params = new URLSearchParams(searchParams.toString());
    // Drop ?code=&state= from the address bar and history whatever the outcome.
    window.history.replaceState(null, "", CALLBACK_PATH);
    completeLogin(params)
      .then(({ returnTo }) => router.replace(returnTo))
      .catch((error: unknown) =>
        setRetryTo((error instanceof LoginCallbackError && error.returnTo) || "/"),
      );
  }, [router, searchParams]);

  if (retryTo !== null) {
    return (
      <Centered>
        <EmptyState headingLevel={1}
          title={vi.auth.callbackFailed}
          action={
            <Button variant="secondary" onClick={() => void startLogin(retryTo).catch(() => undefined)}>
              {vi.auth.retry}
            </Button>
          }
        />
      </Centered>
    );
  }
  return (
    <Centered>
      <EmptyState headingLevel={1} busy title={vi.auth.completing} />
    </Centered>
  );
}

export default function CallbackPage() {
  return (
    <Suspense
      fallback={
        <Centered>
          <EmptyState headingLevel={1} busy title={vi.auth.completing} />
        </Centered>
      }
    >
      <CallbackContent />
    </Suspense>
  );
}
