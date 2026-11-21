// Login start and callback handling for the Authorization Code + PKCE flow.

import { buildAuthorizeUrl, exchangeCode } from "./cognito";
import { getAuthConfig } from "./config";
import { createPkcePair, createState } from "./pkce";
import { sanitizeReturnTo } from "./return-to";
import { setTokens } from "./session";

const PENDING_KEY = "exammaker.auth.pending";

type Pending = { state: string; verifier: string; returnTo: string };

export type LoginCallbackErrorCode =
  | "missing_pending"
  | "state_mismatch"
  | "missing_code"
  | "cognito_error"
  | "token_exchange_failed";

export class LoginCallbackError extends Error {
  readonly code: LoginCallbackErrorCode;
  /** Already sanitized; set only when the pending login data was present. */
  readonly returnTo?: string;

  constructor(code: LoginCallbackErrorCode, returnTo?: string) {
    super(`Login callback failed: ${code}`);
    this.name = "LoginCallbackError";
    this.code = code;
    this.returnTo = returnTo;
  }
}

function isPending(value: unknown): value is Pending {
  if (!value || typeof value !== "object") return false;
  const v = value as Record<string, unknown>;
  return (
    typeof v.state === "string" && typeof v.verifier === "string" && typeof v.returnTo === "string"
  );
}

function takePending(): Pending | null {
  try {
    const raw = sessionStorage.getItem(PENDING_KEY);
    sessionStorage.removeItem(PENDING_KEY);
    if (!raw) return null;
    const parsed: unknown = JSON.parse(raw);
    return isPending(parsed) ? parsed : null;
  } catch {
    return null;
  }
}

export async function startLogin(returnTo: string): Promise<void> {
  const cfg = getAuthConfig();
  if (!cfg) throw new Error("Cognito login is not configured");

  const { verifier, challenge } = await createPkcePair();
  const state = createState();
  const pending: Pending = { state, verifier, returnTo };
  sessionStorage.setItem(PENDING_KEY, JSON.stringify(pending));
  // replace, not assign: the page left behind must not become a Back target, since a
  // restore from the back/forward cache would show it frozen mid-redirect.
  location.replace(buildAuthorizeUrl(cfg, { state, codeChallenge: challenge }));
}

export async function completeLogin(search: URLSearchParams): Promise<{ returnTo: string }> {
  // The pending data is single-use: always removed, whatever the outcome.
  const pending = takePending();
  // Lets a retry after a failure go back to the page the user started from.
  const returnTo = pending ? sanitizeReturnTo(pending.returnTo) : undefined;

  if (search.get("error")) throw new LoginCallbackError("cognito_error", returnTo);
  if (!pending) throw new LoginCallbackError("missing_pending");
  if (search.get("state") !== pending.state) {
    throw new LoginCallbackError("state_mismatch", returnTo);
  }
  const code = search.get("code");
  if (!code) throw new LoginCallbackError("missing_code", returnTo);

  const cfg = getAuthConfig();
  if (!cfg) throw new LoginCallbackError("token_exchange_failed", returnTo);

  let tokens;
  try {
    tokens = await exchangeCode(cfg, { code, codeVerifier: pending.verifier });
  } catch {
    throw new LoginCallbackError("token_exchange_failed", returnTo);
  }
  setTokens(tokens);
  return { returnTo: sanitizeReturnTo(pending.returnTo) };
}
