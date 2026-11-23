import type { components } from "@/lib/api/schema";
import { vi } from "@/messages/vi";

type AiJob = components["schemas"]["AiJob"];

/** A Queued job older than this probably waits for the AI worker to come back (EC2 is off at night). */
export const STALE_QUEUED_MS = 15 * 60 * 1000;

export type JobDisplay = {
  kind: "waiting" | "retrying" | "running" | "failed" | "succeeded";
  attempt: number;
  maxAttempts: number;
  stale: boolean;
  message: string | null;
};

export function describeJob(job: AiJob, now: Date): JobDisplay {
  const base = { attempt: job.attempt, maxAttempts: job.maxAttempts };
  switch (job.status) {
    case "Queued": {
      const stale = now.getTime() - new Date(job.createdAt).getTime() > STALE_QUEUED_MS;
      return job.attempt > 0
        ? { ...base, kind: "retrying", stale, message: job.errorMessage ?? null }
        : { ...base, kind: "waiting", stale, message: null };
    }
    case "Running":
      return { ...base, kind: "running", stale: false, message: null };
    case "Failed":
      return { ...base, kind: "failed", stale: false, message: job.errorMessage ?? vi.ai.failedFallback };
    case "Succeeded":
      return { ...base, kind: "succeeded", stale: false, message: null };
  }
}
