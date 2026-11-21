import { type AuthConfig, logoutUri, redirectUri } from "./config";

export type TokenSet = {
  accessToken: string;
  expiresIn: number;
  refreshToken?: string;
};

export class TokenEndpointError extends Error {
  readonly code: string;
  readonly status: number;

  constructor(code: string, status: number) {
    super(`Token endpoint error: ${code}`);
    this.name = "TokenEndpointError";
    this.code = code;
    this.status = status;
  }
}

const SCOPE = "openid email profile";

export function buildAuthorizeUrl(
  cfg: AuthConfig,
  p: { state: string; codeChallenge: string },
): string {
  const params = new URLSearchParams({
    response_type: "code",
    client_id: cfg.clientId,
    redirect_uri: redirectUri(),
    scope: SCOPE,
    state: p.state,
    code_challenge: p.codeChallenge,
    code_challenge_method: "S256",
  });
  return `${cfg.domain}/oauth2/authorize?${params.toString()}`;
}

export function buildLogoutUrl(cfg: AuthConfig): string {
  const params = new URLSearchParams({
    client_id: cfg.clientId,
    logout_uri: logoutUri(),
  });
  return `${cfg.domain}/logout?${params.toString()}`;
}

function postForm(
  cfg: AuthConfig,
  path: string,
  body: Record<string, string>,
  signal?: AbortSignal,
): Promise<Response> {
  return fetch(`${cfg.domain}${path}`, {
    method: "POST",
    headers: { "Content-Type": "application/x-www-form-urlencoded" },
    body: new URLSearchParams(body).toString(),
    signal,
  });
}

async function readErrorCode(res: Response): Promise<string> {
  try {
    const data: unknown = await res.json();
    if (data && typeof data === "object" && "error" in data && typeof data.error === "string") {
      return data.error;
    }
  } catch {
    // Non-JSON error body; fall through to the generic code.
  }
  return "unknown_error";
}

// Network failures (fetch rejects) propagate unchanged so callers can tell them apart
// from an explicit rejection by Cognito.
async function requestTokens(cfg: AuthConfig, body: Record<string, string>): Promise<TokenSet> {
  const res = await postForm(cfg, "/oauth2/token", body);
  if (!res.ok) {
    throw new TokenEndpointError(await readErrorCode(res), res.status);
  }
  const data = (await res.json()) as {
    access_token: string;
    expires_in: number;
    refresh_token?: string;
  };
  return {
    accessToken: data.access_token,
    expiresIn: data.expires_in,
    refreshToken: data.refresh_token,
  };
}

export function exchangeCode(
  cfg: AuthConfig,
  p: { code: string; codeVerifier: string },
): Promise<TokenSet> {
  return requestTokens(cfg, {
    grant_type: "authorization_code",
    client_id: cfg.clientId,
    code: p.code,
    code_verifier: p.codeVerifier,
    redirect_uri: redirectUri(),
  });
}

export function refreshTokens(cfg: AuthConfig, refreshToken: string): Promise<TokenSet> {
  return requestTokens(cfg, {
    grant_type: "refresh_token",
    client_id: cfg.clientId,
    refresh_token: refreshToken,
  });
}

export async function revokeToken(
  cfg: AuthConfig,
  refreshToken: string,
  signal?: AbortSignal,
): Promise<void> {
  const res = await postForm(
    cfg,
    "/oauth2/revoke",
    { token: refreshToken, client_id: cfg.clientId },
    signal,
  );
  if (!res.ok) {
    throw new TokenEndpointError(await readErrorCode(res), res.status);
  }
}
