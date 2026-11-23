"use client";

import { useQueries, useQuery } from "@tanstack/react-query";
import { useSearchParams } from "next/navigation";
import { Suspense, useMemo } from "react";

import {
  GeneratedQuestionCard,
  questionKey,
  QuestionUnavailableError,
} from "@/components/ai/generated-question-card";
import { STATUS_BADGE } from "@/components/ai/job-list";
import { JobStatusPanel } from "@/components/ai/job-status-panel";
import { nextPollDelay } from "@/lib/ai/polling";
import { fetchAllSubjects, fetchAllTopics } from "@/lib/api/catalog";
import { api } from "@/lib/api/client";
import { getApiErrorMessage } from "@/lib/api/error";
import { useAuth } from "@/lib/auth/auth-context";
import { vi } from "@/messages/vi";

class JobNotFoundError extends Error {}

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
    return <p className="text-sm text-slate-600">{vi.ai.jobNotFound}</p>;
  }
  if (!job) {
    return jobQuery.isError ? (
      <p className="text-sm text-red-700">{vi.ai.reconnecting}</p>
    ) : (
      <p className="text-sm text-slate-500">{vi.common.loading}</p>
    );
  }

  const subjectName = subjectsQuery.data?.find((s) => s.id === job.params.subjectId)?.name ?? "—";
  const topicName = topicsQuery.data?.find((t) => t.id === job.params.topicId)?.name ?? "—";
  // A question deleted after it was cached keeps stale `data`; count only ones still readable.
  const available = questionQueries
    .filter((q) => !(q.error instanceof QuestionUnavailableError))
    .map((q) => q.data)
    .filter((q) => q != null);
  const reviewed = available.filter((q) => q.aiReviewedByAuthor).length;

  return (
    <section className="space-y-6">
      <header className="space-y-2">
        <div className="flex flex-wrap items-center gap-3">
          <h1 className="text-2xl font-bold text-slate-900">{vi.ai.jobTitle}</h1>
          <span className={`rounded px-2 py-0.5 text-xs font-medium ${STATUS_BADGE[job.status]}`}>
            {vi.ai.jobStatuses[job.status]}
          </span>
        </div>
        <p className="text-sm text-slate-600">
          {vi.ai.params}: {subjectName} · {vi.common.gradePrefix} {job.params.grade} · {topicName} ·{" "}
          {vi.questions.types[job.params.type]} · {vi.questions.difficulties[job.params.difficulty]} · {vi.ai.count}{" "}
          {job.params.count}
        </p>
        {job.params.note && (
          <div className="text-sm text-slate-600">
            <span className="font-medium">{vi.ai.noteLabel}:</span>
            {/* Teacher input is untrusted: plain text only, never Markdown. */}
            <p className="whitespace-pre-wrap">{job.params.note}</p>
          </div>
        )}
        {jobQuery.isError && <p className="text-sm text-amber-800">{vi.ai.reconnecting}</p>}
      </header>

      {/* dataUpdatedAt changes on every poll even when the job JSON is identical (structural sharing keeps
          `data`), so reading it re-renders the page and lets a waiting job turn "stale" while it stays open. */}
      <JobStatusPanel job={job} now={new Date(jobQuery.dataUpdatedAt)} />

      {questionIds.length > 0 && (
        <>
          <p className="text-sm font-medium text-slate-700">
            {vi.ai.summary(available.length, job.duplicatesDropped, reviewed)}
          </p>
          <div className="space-y-4">
            {questionQueries.map((q, i) =>
              q.isPending ? (
                <p key={questionIds[i]} className="text-sm text-slate-500">
                  {vi.common.loading}
                </p>
              ) : q.error instanceof QuestionUnavailableError ? (
                <GeneratedQuestionCard key={questionIds[i]} question={null} index={i + 1} meId={me?.id ?? ""} />
              ) : q.isError && !q.data ? (
                // Transient failure (network/5xx): offer a retry instead of claiming the question is gone.
                <div key={questionIds[i]} className="flex items-center gap-3 rounded border border-red-200 bg-red-50 p-4 text-sm text-red-800">
                  <span className="font-semibold">{vi.ai.questionLabel(i + 1)}</span>
                  <span>{vi.ai.questionLoadFailed}</span>
                  <button type="button" onClick={() => void q.refetch()} className="underline">
                    {vi.ai.retry}
                  </button>
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
    </section>
  );
}

export default function AiJobPage() {
  return (
    <Suspense fallback={<p>{vi.common.loading}</p>}>
      <JobPageInner />
    </Suspense>
  );
}
