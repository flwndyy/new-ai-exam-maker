import { vi } from "@/messages/vi";

interface ProblemPayload {
  code?: string;
  title?: string;
  detail?: string;
  errors?: Record<string, string[]>;
}

const FIELD_LABELS: Record<string, string> = {
  stem: vi.questions.stem,
  topicId: vi.questions.topic,
  subjectId: vi.questions.subject,
  grade: vi.questions.grade,
  type: vi.questions.type,
  difficulty: vi.questions.difficulty,
  payload: vi.questions.answerContent,
  explanation: vi.questions.explanation,
  note: vi.review.noteLabel,
  email: vi.admin.email,
  fullName: vi.admin.fullName,
  role: vi.admin.role,
  departmentId: vi.admin.department,
  name: vi.admin.name,
  sortOrder: vi.admin.sortOrder,
};

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
        const label = FIELD_LABELS[field];
        return label ? `${label}: ${joined}` : joined;
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
