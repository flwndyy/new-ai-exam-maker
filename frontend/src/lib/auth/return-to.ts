// Guards post-login navigation against open redirects: only same-origin
// relative paths are accepted, everything else falls back to "/".

const FALLBACK = "/";
const CALLBACK_PREFIX = "/auth/callback";

export function sanitizeReturnTo(value: string | null | undefined): string {
  if (!value || !value.startsWith("/") || value.startsWith("//") || value.startsWith("/\\")) {
    return FALLBACK;
  }

  let url: URL;
  try {
    url = new URL(value, window.location.origin);
  } catch {
    return FALLBACK;
  }
  if (url.origin !== window.location.origin) {
    return FALLBACK;
  }
  if (url.pathname.startsWith(CALLBACK_PREFIX)) {
    return FALLBACK;
  }
  // Re-validate after normalization: dot segments can turn "/.//evil" into "//evil".
  const result = url.pathname + url.search + url.hash;
  if (!result.startsWith("/") || result.startsWith("//") || result.startsWith("/\\")) {
    return FALLBACK;
  }
  return result;
}
