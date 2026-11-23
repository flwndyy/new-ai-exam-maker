"use client";

import { useRouter, useSearchParams } from "next/navigation";
import { Suspense, useEffect, useRef, useState } from "react";

import { completeLogin, LoginCallbackError, startLogin } from "@/lib/auth/login";
import { vi } from "@/messages/vi";

const CALLBACK_PATH = "/auth/callback/";

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
      <div className="py-12 text-center text-slate-600">
        <p>{vi.auth.callbackFailed}</p>
        <button
          type="button"
          className="mt-4 rounded border border-slate-300 px-3 py-1 hover:bg-slate-100"
          onClick={() => void startLogin(retryTo).catch(() => undefined)}
        >
          {vi.auth.retry}
        </button>
      </div>
    );
  }
  return <div className="py-12 text-center text-slate-600">{vi.auth.completing}</div>;
}

export default function CallbackPage() {
  return (
    <Suspense fallback={<div className="py-12 text-center text-slate-600">{vi.auth.completing}</div>}>
      <CallbackContent />
    </Suspense>
  );
}
