import { vi } from "@/messages/vi";

interface ProblemPayload {
  title?: string;
  detail?: string;
  errors?: Record<string, string[]>;
}

/** Extract a safe Vietnamese/API error message from an openapi-fetch ProblemDetails payload. */
export function getApiErrorMessage(error: unknown, fallback = vi.common.requestFailed): string {
  if (!error || typeof error !== "object") return fallback;
  const problem = error as ProblemPayload;
  if (problem.errors) {
    const first = Object.values(problem.errors).flat().find(Boolean);
    if (first) return first;
  }
  return problem.detail || problem.title || fallback;
}
