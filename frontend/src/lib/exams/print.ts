export type PrintMode = "student" | "answer";

export type PrintParams = { ok: true; vid: string; mode: PrintMode; examId: string | null } | { ok: false };

/** Validate the print route query; an invalid link must never trigger an API call. */
export function parsePrintParams(search: URLSearchParams): PrintParams {
  const vid = search.get("vid");
  const mode = search.get("mode");
  if (!vid || (mode !== "student" && mode !== "answer")) return { ok: false };
  return { ok: true, vid, mode, examId: search.get("id") || null };
}

/** Static export: the print page is addressed by query string, never by a dynamic segment. */
export function printUrl(examId: string, vid: string, mode: PrintMode): string {
  return `/exams/print/?id=${encodeURIComponent(examId)}&vid=${encodeURIComponent(vid)}&mode=${mode}`;
}

/** Optional build-time school name for the sheet header; blank means "omit the line". */
export function schoolName(): string | null {
  // NEXT_PUBLIC_* values are inlined at build time, so this must stay a literal property access.
  const name = process.env.NEXT_PUBLIC_SCHOOL_NAME?.trim();
  return name ? name : null;
}
