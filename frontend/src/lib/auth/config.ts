export interface AuthConfig {
  domain: string;
  clientId: string;
}

// NEXT_PUBLIC_* values are inlined at build time, so they must be read as literal
// `process.env.NAME` accesses. Reading at call time keeps the function testable.
export function getAuthConfig(): AuthConfig | null {
  const rawDomain = process.env.NEXT_PUBLIC_COGNITO_DOMAIN;
  const clientId = process.env.NEXT_PUBLIC_COGNITO_CLIENT_ID;
  if (!rawDomain || !clientId) return null;
  if (!rawDomain.startsWith("https://")) return null;
  return { domain: rawDomain.replace(/\/+$/, ""), clientId };
}

export function redirectUri(): string {
  return `${window.location.origin}/auth/callback/`;
}

export function logoutUri(): string {
  return `${window.location.origin}/`;
}
