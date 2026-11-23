import { vi } from "@/messages/vi";

interface ProblemPayload {
  code?: string;
  title?: string;
  detail?: string;
  errors?: Record<string, string[]>;
}

/** Extract the machine-readable ProblemDetails error code when present. */
export function getApiErrorCode(error: unknown): string | undefined {
  if (!error || typeof error !== "object") return undefined;
  return (error as ProblemPayload).code;
}

/** Extract a safe Vietnamese/API error message from an openapi-fetch ProblemDetails payload. */
export function getApiErrorMessage(error: unknown, fallback = vi.common.requestFailed): string {
  if (!error || typeof error !== "object") return fallback;
  const problem = error as ProblemPayload;
  if (problem.errors) {
    const entries = Object.entries(problem.errors)
      .map(([field, messages]) => {
        const joined = messages.filter(Boolean).join(", ");
        if (!joined) return "";
        return field ? `${field}: ${joined}` : joined;
      })
      .filter(Boolean);
    if (entries.length > 0) return entries.join("; ");
  }
  return problem.detail || problem.title || fallback;
}

const RAW_NETWORK_ERROR_RE = /^(failed to fetch|fetch failed|load failed|network\s*error|the internet connection appears to be offline)/i;

/** Map raw browser/network errors (e.g. TypeError: Failed to fetch) to a localized fallback message. */
export function getUiErrorMessage(error: unknown, fallback = vi.common.requestFailed): string {
  if (error instanceof Error) {
    const msg = error.message.trim();
    if (!msg || RAW_NETWORK_ERROR_RE.test(msg)) {
      return fallback;
    }
    if (error instanceof TypeError) {
      console.error(error);
      return fallback;
    }
    return msg;
  }
  return getApiErrorMessage(error, fallback);
}
