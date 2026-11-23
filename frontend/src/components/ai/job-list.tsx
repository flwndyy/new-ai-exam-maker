"use client";

import { keepPreviousData, useQuery } from "@tanstack/react-query";
import Link from "next/link";
import { useState } from "react";

import { isJobFinished } from "@/lib/ai/polling";
import { api } from "@/lib/api/client";
import { getApiErrorMessage, getUiErrorMessage } from "@/lib/api/error";
import type { components } from "@/lib/api/schema";
import { vi } from "@/messages/vi";

type AiJobStatus = components["schemas"]["AiJobStatus"];

/** Refetch interval of the job list while any listed job is still Queued/Running. */
export const JOB_LIST_REFRESH_MS = 15000;

const dateFormat = new Intl.DateTimeFormat("vi-VN", {
  dateStyle: "short",
  timeStyle: "short",
  timeZone: "Asia/Ho_Chi_Minh",
});

export const STATUS_BADGE: Record<AiJobStatus, string> = {
  Queued: "bg-slate-100 text-slate-700",
  Running: "bg-blue-100 text-blue-800",
  Succeeded: "bg-emerald-100 text-emerald-800",
  Failed: "bg-red-100 text-red-800",
};

/** "Job của tôi": the requester's AI jobs, newest first (server data, so it survives leaving the page). */
export function JobList({ subjectNames }: { subjectNames: Map<string, string> }) {
  const [page, setPage] = useState(1);
  const jobsQuery = useQuery({
    queryKey: ["ai", "jobs", page],
    placeholderData: keepPreviousData,
    queryFn: async () => {
      const { data, error } = await api.GET("/ai/jobs", { params: { query: { page, pageSize: 20 } } });
      if (error || !data) throw new Error(getApiErrorMessage(error));
      return data;
    },
    refetchInterval: (query) =>
      query.state.data?.items.some((job) => !isJobFinished(job.status)) ? JOB_LIST_REFRESH_MS : false,
  });

  const data = jobsQuery.data;
  return (
    <section className="space-y-3">
      <h2 className="text-lg font-semibold text-slate-900">{vi.ai.myJobs}</h2>
      {jobsQuery.isLoading && <p className="text-sm text-slate-500">{vi.common.loading}</p>}
      {jobsQuery.isError && <p className="text-sm text-red-700">{getUiErrorMessage(jobsQuery.error)}</p>}
      {data && data.items.length === 0 && <p className="text-sm text-slate-500">{vi.ai.noJobs}</p>}
      {data && data.items.length > 0 && (
        <div className="overflow-x-auto">
          <table className="min-w-full text-left text-sm">
            <thead className="border-b border-slate-200 text-slate-500">
              <tr>
                <th className="px-2 py-2">{vi.ai.createdAt}</th>
                <th className="px-2 py-2">{vi.ai.request}</th>
                <th className="px-2 py-2">{vi.common.status}</th>
                <th className="px-2 py-2">{vi.common.actions}</th>
              </tr>
            </thead>
            <tbody>
              {data.items.map((job) => (
                <tr key={job.id} className="border-b border-slate-100">
                  <td className="px-2 py-2 whitespace-nowrap">{dateFormat.format(new Date(job.createdAt))}</td>
                  <td className="px-2 py-2">
                    {subjectNames.get(job.params.subjectId) ?? "—"} · {vi.common.gradePrefix} {job.params.grade} ·{" "}
                    {vi.questions.types[job.params.type]} · {job.params.count}
                  </td>
                  <td className="px-2 py-2">
                    <span className={`rounded px-2 py-0.5 text-xs font-medium ${STATUS_BADGE[job.status]}`}>
                      {vi.ai.jobStatuses[job.status]}
                    </span>
                  </td>
                  <td className="px-2 py-2">
                    <Link href={`/ai/job/?id=${job.id}`} className="text-blue-700 hover:underline">
                      {vi.common.view}
                    </Link>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {data && data.totalPages > 1 && (
        <div className="flex items-center gap-3 text-sm">
          <button type="button" disabled={page <= 1} onClick={() => setPage((p) => p - 1)} className="disabled:opacity-50">
            {vi.common.previousPage}
          </button>
          <span>
            {vi.common.pageLabel} {page}/{data.totalPages}
          </span>
          <button
            type="button"
            disabled={page >= data.totalPages}
            onClick={() => setPage((p) => p + 1)}
            className="disabled:opacity-50"
          >
            {vi.common.nextPage}
          </button>
        </div>
      )}
    </section>
  );
}
