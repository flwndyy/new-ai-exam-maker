import type { Middleware } from "openapi-fetch";

import { clearSession, forceRefresh } from "@/lib/auth/session";
import { getAccessToken } from "./auth-token";

/**
 * Attaches the bearer token and, on a 401, refreshes the session once and replays the
 * request once. The replay goes straight through `fetchImpl` (not through the client's
 * middleware chain), so it can never loop.
 */
export function createAuthMiddleware(
  fetchImpl: typeof fetch = (...args) => globalThis.fetch(...args),
): Middleware {
  // Pristine copies of in-flight requests, keyed by openapi-fetch's per-request id.
  const pending = new Map<string, Request>();

  return {
    async onRequest({ request, id }) {
      let token: string | null = null;
      try {
        token = await getAccessToken();
      } catch {
        // Token expired and the refresh failed transiently: send the request anyway; the
        // API answers 401 and the normal retry path below applies.
      }
      if (token) request.headers.set("Authorization", `Bearer ${token}`);
      pending.set(id, request.clone());
      return request;
    },

    async onResponse({ response, id }) {
      const original = pending.get(id);
      pending.delete(id);
      if (response.status !== 401 || !original) return response;

      let freshToken: string | null;
      try {
        freshToken = await forceRefresh();
      } catch {
        // Transient refresh failure: keep the session, surface the original 401.
        return response;
      }
      // null: no refresh token or a definitive rejection (session already signedOut).
      if (!freshToken) return response;

      original.headers.set("Authorization", `Bearer ${freshToken}`);
      let retried: Response;
      try {
        retried = await fetchImpl(original);
      } catch {
        return response;
      }
      // A freshly refreshed token that is still refused means the account has no access.
      if (retried.status === 401) clearSession("denied");
      return retried;
    },

    onError({ id }) {
      pending.delete(id);
    },
  };
}
