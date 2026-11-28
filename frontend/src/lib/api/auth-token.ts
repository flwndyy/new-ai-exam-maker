// Token hook for the API client. B2 (Cognito login) registers a provider here;
// B1 only reserves the seam: no token is stored and no login logic lives here.

export type AccessTokenProvider = () => string | null | Promise<string | null>;

let provider: AccessTokenProvider | null = null;

export function setAccessTokenProvider(next: AccessTokenProvider | null): void {
  provider = next;
}

export async function getAccessToken(): Promise<string | null> {
  return provider ? await provider() : null;
}
