"use client";

import { Notice } from "@/components/ui/notice";
import { describeJob } from "@/lib/ai/job-status";
import type { components } from "@/lib/api/schema";
import { vi } from "@/messages/vi";

type AiJob = components["schemas"]["AiJob"];

/** Decorative pulsing dot for a running job; the status itself is always given as text next to it. */
export function RunningDot() {
  return <span aria-hidden="true" className="inline-block size-2 shrink-0 rounded-full bg-wait motion-safe:animate-pulse" />;
}

/** Status strip for a job that is not (or not yet) a successful batch with questions. Regenerate lives in the page header. */
export function JobStatusPanel({ job, now }: { job: AiJob; now: Date }) {
  const display = describeJob(job, now);
  const title = vi.ai.jobStatuses[job.status];

  switch (display.kind) {
    case "waiting":
    case "retrying":
      return (
        <div className="space-y-2">
          <Notice tone="info" title={title}>
            <p>{display.kind === "waiting" ? vi.ai.waiting : vi.ai.retrying(display.attempt, display.maxAttempts)}</p>
            {display.message && <p>{display.message}</p>}
          </Notice>
          {display.stale && <Notice tone="warn">{vi.ai.staleHint}</Notice>}
        </div>
      );
    case "running":
      return (
        <Notice tone="info" title={title}>
          <p className="flex items-center gap-2">
            <RunningDot />
            <span>{vi.ai.running(display.attempt, display.maxAttempts)}</span>
          </p>
        </Notice>
      );
    case "failed":
      return (
        <Notice tone="error" title={title}>
          {display.message}
        </Notice>
      );
    case "succeeded":
      return job.questionIds?.length ? null : <Notice tone="info">{vi.ai.allDuplicates}</Notice>;
  }
}
