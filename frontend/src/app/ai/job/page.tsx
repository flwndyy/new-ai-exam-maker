"use client";

import { useQueries, useQuery } from "@tanstack/react-query";
import { useSearchParams } from "next/navigation";
import { Suspense, useMemo } from "react";

import {
  GeneratedQuestionCard,
  questionKey,
  QuestionUnavailableError,
} from "@/components/ai/generated-question-card";
import { JobStatusPanel } from "@/components/ai/job-status-panel";
import { PageHeader } from "@/components/shell/page-header";
import { Button, ButtonLink } from "@/components/ui/button";
import { EmptyState } from "@/components/ui/empty-state";
import { Notice } from "@/components/ui/notice";
import { nextPollDelay } from "@/lib/ai/polling";
import { fetchAllSubjects, fetchAllTopics } from "@/lib/api/catalog";
import { api } from "@/lib/api/client";
import { getApiErrorMessage } from "@/lib/api/error";
import { useAuth } from "@/lib/auth/auth-context";
import { vi } from "@/messages/vi";

class JobNotFoundError extends Error {}

const dateFormat = new Intl.DateTimeFormat("vi-VN", {
  dateStyle: "short",
  timeStyle: "short",
  timeZone: "Asia/Ho_Chi_Minh",
});

const BODY = "px-4 py-5 sm:px-7 sm:py-6";
const CRUMB = { href: "/ai/", label: vi.nav.ai };

function JobPageInner() {
  const id = useSearchParams().get("id");
  const { me } = useAuth();

  const jobQuery = useQuery({
    queryKey: ["ai", "job", id],
    enabled: Boolean(id),
    queryFn: async () => {
      const { data, error, response } = await api.GET("/ai/jobs/{id}", { params: { path: { id: id! } } });
      if (response.status === 404) throw new JobNotFoundError(vi.ai.jobNotFound);
      if (error || !data) throw new Error(getApiErrorMessage(error));
      return data;
    },
    // Backoff 2s/4s/8s/15s; stop once Succeeded/Failed or the job does not exist. Network errors keep polling.
    refetchInterval: (query) => {
      if (query.state.error instanceof JobNotFoundError) return false;
      const polls = query.state.dataUpdateCount + query.state.errorUpdateCount;
      return nextPollDelay(polls - 1, query.state.data?.status);
    },
  });
  const job = jobQuery.data;

  const subjectsQuery = useQuery({
    queryKey: ["catalog", "subjects", me?.departmentId ?? null],
    enabled: Boolean(job),
    queryFn: () => fetchAllSubjects(me?.departmentId ?? undefined),
  });
  const topicsQuery = useQuery({
    queryKey: ["catalog", "topics", job?.params.subjectId, job?.params.grade],
    enabled: Boolean(job),
    queryFn: () => fetchAllTopics(job!.params.subjectId, job!.params.grade),
  });

  const questionIds = useMemo(() => (job?.status === "Succeeded" ? (job.questionIds ?? []) : []), [job]);
  const questionQueries = useQueries({
    queries: questionIds.map((qid) => ({
      queryKey: questionKey(qid),
      queryFn: async () => {
        const { data, error, response } = await api.GET("/questions/{id}", { params: { path: { id: qid } } });
        // Deleted or no longer visible: shown as unavailable instead of failing the whole batch.
        if (response.status === 404 || response.status === 403) throw new QuestionUnavailableError();
        if (error || !data) throw new Error(getApiErrorMessage(error));
        return data;
      },
      // The author may have edited, reviewed or deleted the question on another page: always reload on return.
      refetchOnMount: "always" as const,
      retry: (count: number, error: Error) => !(error instanceof QuestionUnavailableError) && count < 1,
    })),
  });

  if (!id || (jobQuery.error instanceof JobNotFoundError && !job)) {
    return (
      <>
        <PageHeader crumb={CRUMB} title={vi.ai.jobTitle} />
        <div className={BODY}>
          <EmptyState title={vi.ai.jobNotFound} />
        </div>
      </>
    );
  }
  if (!job) {
    return (
      <>
        <PageHeader crumb={CRUMB} title={vi.ai.jobTitle} />
        <div className={BODY}>
          {jobQuery.isError ? <Notice tone="error">{vi.ai.reconnecting}</Notice> : <EmptyState busy title={vi.common.loading} />}
        </div>
      </>
    );
  }

  const subjectName = subjectsQuery.data?.find((s) => s.id === job.params.subjectId)?.name;
  const topicName = topicsQuery.data?.find((t) => t.id === job.params.topicId)?.name;
  const heading = [subjectName, `${vi.common.gradePrefix} ${job.params.grade}`, topicName].filter(Boolean).join(", ");
  // A question deleted after it was cached keeps stale `data`; count only ones still readable.
  const available = questionQueries
    .filter((q) => !(q.error instanceof QuestionUnavailableError))
    .map((q) => q.data)
    .filter((q) => q != null);
  const reviewed = available.filter((q) => q.aiReviewedByAuthor).length;

  return (
    <>
      <PageHeader
        crumb={CRUMB}
        title={heading}
        description={vi.ai.jobMeta(
          vi.questions.types[job.params.type],
          vi.questions.difficulties[job.params.difficulty],
          job.params.count,
          dateFormat.format(new Date(job.createdAt)),
        )}
        actions={
          <ButtonLink href={`/ai/?from=${job.id}`} variant="boardSecondary">
            {vi.ai.regenerate}
          </ButtonLink>
        }
      />
      <div className={`${BODY} space-y-4`}>
        {jobQuery.isError && <Notice tone="warn">{vi.ai.reconnecting}</Notice>}

        {job.params.note && (
          <div className="text-[13.5px]">
            <p className="font-semibold text-ink-soft">{vi.ai.noteLabel}</p>
            {/* Teacher input is untrusted: plain text only, never Markdown. */}
            <p className="whitespace-pre-wrap">{job.params.note}</p>
          </div>
        )}

        {/* dataUpdatedAt changes on every poll even when the job JSON is identical (structural sharing keeps
            `data`), so reading it re-renders the page and lets a waiting job turn "stale" while it stays open. */}
        <JobStatusPanel job={job} now={new Date(jobQuery.dataUpdatedAt)} />

        {questionIds.length > 0 && (
          <>
            <Notice
              tone="success"
              title={vi.ai.jobStatuses.Succeeded}
              action={<span className="font-semibold tabular-nums">{vi.ai.reviewProgress(reviewed, available.length)}</span>}
            >
              {vi.ai.summary(available.length, job.duplicatesDropped)}
            </Notice>
            <div className="space-y-3">
              {questionQueries.map((q, i) =>
                q.isPending ? (
                  <p key={questionIds[i]} className="text-[13.5px] text-muted">
                    {vi.common.loading}
                  </p>
                ) : q.error instanceof QuestionUnavailableError ? (
                  <GeneratedQuestionCard key={questionIds[i]} question={null} index={i + 1} meId={me?.id ?? ""} />
                ) : q.isError && !q.data ? (
                  // Transient failure (network/5xx): offer a retry instead of claiming the question is gone.
                  <div key={questionIds[i]} className="space-y-2 rounded-lg border border-line bg-surface p-4">
                    <h3 className="text-[15px] font-semibold">{vi.ai.questionLabel(i + 1)}</h3>
                    <Notice
                      tone="error"
                      action={
                        <Button variant="secondary" size="sm" onClick={() => void q.refetch()}>
                          {vi.ai.retry}
                        </Button>
                      }
                    >
                      {vi.ai.questionLoadFailed}
                    </Notice>
                  </div>
                ) : (
                  // A failed reload keeps the cached copy visible but flagged, with review blocked until a reload succeeds.
                  <GeneratedQuestionCard
                    key={questionIds[i]}
                    question={q.data ?? null}
                    index={i + 1}
                    meId={me?.id ?? ""}
                    refreshFailed={q.isError}
                    refreshing={q.isFetching}
                    onRetry={() => void q.refetch()}
                  />
                ),
              )}
            </div>
          </>
        )}
      </div>
    </>
  );
}

export default function AiJobPage() {
  return (
    <Suspense fallback={<EmptyState busy title={vi.common.loading} />}>
      <JobPageInner />
    </Suspense>
  );
}
