"use client";

import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { useState } from "react";

import { RunningDot } from "@/components/ai/job-status-panel";
import { EmptyState } from "@/components/ui/empty-state";
import { ListRow } from "@/components/ui/list-row";
import { Notice } from "@/components/ui/notice";
import { Pager } from "@/components/ui/pager";
import { StatusText } from "@/components/ui/status-text";
import { isJobFinished } from "@/lib/ai/polling";
import { api } from "@/lib/api/client";
import { getApiErrorMessage, getUiErrorMessage } from "@/lib/api/error";
import { aiJobTone } from "@/lib/ui/status";
import { vi } from "@/messages/vi";

/** Refetch interval of the job list while any listed job is still Queued/Running. */
export const JOB_LIST_REFRESH_MS = 15000;

const dateFormat = new Intl.DateTimeFormat("vi-VN", {
  dateStyle: "short",
  timeStyle: "short",
  timeZone: "Asia/Ho_Chi_Minh",
});

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
  const hasUnfinished = data?.items.some((job) => !isJobFinished(job.status)) ?? false;
  return (
    <section>
      <div className="mb-3 flex items-baseline justify-between gap-2">
        <h2 className="text-[15px] font-semibold">{vi.ai.myJobs}</h2>
        {hasUnfinished && <span className="text-[12.5px] text-muted">{vi.ai.jobsAutoRefresh}</span>}
      </div>
      {jobsQuery.isLoading && <p className="text-[13.5px] text-muted">{vi.common.loading}</p>}
      {jobsQuery.isError && <Notice tone="error">{getUiErrorMessage(jobsQuery.error)}</Notice>}
      {data && data.items.length === 0 && <EmptyState title={vi.ai.noJobs} className="min-h-[120px]" />}
      {data && data.items.length > 0 && (
        <div>
          {data.items.map((job) => {
            const tone = aiJobTone(job.status);
            const subject = subjectNames.get(job.params.subjectId);
            const gradeLabel = `${vi.common.gradePrefix} ${job.params.grade}`;
            return (
              <ListRow
                key={job.id}
                tone={tone}
                href={`/ai/job/?id=${job.id}`}
                title={subject ? `${subject}, ${gradeLabel}` : gradeLabel}
                meta={vi.ai.jobMeta(
                  vi.questions.types[job.params.type],
                  vi.questions.difficulties[job.params.difficulty],
                  job.params.count,
                  dateFormat.format(new Date(job.createdAt)),
                )}
                reason={job.status === "Failed" ? job.errorMessage : undefined}
                end={
                  <StatusText tone={tone} className="inline-flex items-center gap-1.5">
                    {job.status === "Running" && <RunningDot />}
                    {vi.ai.jobStatuses[job.status]}
                  </StatusText>
                }
              />
            );
          })}
        </div>
      )}
      {data && data.totalPages > 1 && <Pager page={page} totalPages={data.totalPages} onPage={setPage} />}
    </section>
  );
}
