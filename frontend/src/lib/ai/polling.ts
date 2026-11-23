import type { components } from "@/lib/api/schema";

type AiJobStatus = components["schemas"]["AiJobStatus"];

/** Delay before each next poll of GET /ai/jobs/{id}; the last value repeats (docs/context.md B8). */
export const POLL_DELAYS_MS = [2000, 4000, 8000, 15000] as const;

export function isJobFinished(status: AiJobStatus): boolean {
  return status === "Succeeded" || status === "Failed";
}

/** Delay before the next poll, or false once the job is finished. `pollCount` counts responses so far, from 0. */
export function nextPollDelay(pollCount: number, status: AiJobStatus | undefined): number | false {
  if (status !== undefined && isJobFinished(status)) return false;
  const index = Math.min(Math.max(pollCount, 0), POLL_DELAYS_MS.length - 1);
  return POLL_DELAYS_MS[index];
}
