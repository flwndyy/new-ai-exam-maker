"use client";

import Link from "next/link";

import { describeJob } from "@/lib/ai/job-status";
import type { components } from "@/lib/api/schema";
import { vi } from "@/messages/vi";

type AiJob = components["schemas"]["AiJob"];

/** Status line for a job that is not (or not yet) a successful batch with questions. */
export function JobStatusPanel({ job, now }: { job: AiJob; now: Date }) {
  const display = describeJob(job, now);
  const regenerate = (
    <Link href={`/ai/?from=${job.id}`} className="text-sm text-blue-700 hover:underline">
      {vi.ai.regenerate}
    </Link>
  );

  switch (display.kind) {
    case "waiting":
    case "retrying":
      return (
        <div className="space-y-1 rounded border border-slate-200 bg-slate-50 p-4 text-sm text-slate-700">
          <p className="flex items-center gap-2">
            <span aria-hidden className="inline-block h-3 w-3 animate-spin rounded-full border-2 border-slate-400 border-t-transparent" />
            <span>{display.kind === "waiting" ? vi.ai.waiting : vi.ai.retrying(display.attempt, display.maxAttempts)}</span>
          </p>
          {display.message && <p>{display.message}</p>}
          {display.stale && <p className="text-amber-800">{vi.ai.staleHint}</p>}
        </div>
      );
    case "running":
      return (
        <p className="flex items-center gap-2 rounded border border-blue-200 bg-blue-50 p-4 text-sm text-blue-900">
          <span aria-hidden className="inline-block h-3 w-3 animate-spin rounded-full border-2 border-blue-400 border-t-transparent" />
          <span>{vi.ai.running(display.attempt, display.maxAttempts)}</span>
        </p>
      );
    case "failed":
      return (
        <div role="alert" className="space-y-2 rounded border border-red-200 bg-red-50 p-4 text-sm text-red-800">
          <p>{display.message}</p>
          {regenerate}
        </div>
      );
    case "succeeded":
      return job.questionIds?.length ? null : (
        <div className="space-y-2 rounded border border-slate-200 bg-slate-50 p-4 text-sm text-slate-700">
          <p>{vi.ai.allDuplicates}</p>
          {regenerate}
        </div>
      );
  }
}
