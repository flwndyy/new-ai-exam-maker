"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useSearchParams } from "next/navigation";
import { Suspense, useId, useState } from "react";

import { MathMarkdown } from "@/components/math-markdown";
import { QuestionPayloadView } from "@/components/questions/question-payload-view";
import { PageHeader } from "@/components/shell/page-header";
import { Button } from "@/components/ui/button";
import { EmptyState } from "@/components/ui/empty-state";
import { Checkbox, Field, Textarea } from "@/components/ui/field";
import { Notice } from "@/components/ui/notice";
import { Panel } from "@/components/ui/panel";
import { StatusText } from "@/components/ui/status-text";
import { Tag, type TagTone } from "@/components/ui/tag";
import { fetchAllTopics } from "@/lib/api/catalog";
import { api } from "@/lib/api/client";
import { getApiErrorMessage, getUiErrorMessage } from "@/lib/api/error";
import type { components } from "@/lib/api/schema";
import { useAuth } from "@/lib/auth/auth-context";
import { questionTone } from "@/lib/ui/status";
import { vi } from "@/messages/vi";

type Question = components["schemas"]["Question"];
type AiVerification = components["schemas"]["AiVerification"];

const AI_FLAG_TONE: Record<AiVerification, TagTone> = { NotChecked: "neutral", Agree: "ok", Disagree: "rej" };

const dateFormat = new Intl.DateTimeFormat("vi-VN", {
  dateStyle: "short",
  timeStyle: "short",
  timeZone: "Asia/Ho_Chi_Minh",
});

const BODY = "px-4 py-5 sm:px-7 sm:py-6";
const CRUMB = { href: "/review/", label: vi.review.title };

function formatSentAt(value?: string | null): string | null {
  if (!value) return null;
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? null : dateFormat.format(date);
}

/** Bind checklist and note state to one submission round via parent key (`id + submittedAt + status`). */
function QuestionDecisionForm({
  question,
  onSuccess,
  onError,
}: {
  question: Question;
  onSuccess: (message: string) => void;
  onError: (message: string) => void;
}) {
  const queryClient = useQueryClient();
  const [checks, setChecks] = useState<boolean[]>(vi.review.checklistItems.map(() => false));
  const [note, setNote] = useState("");
  const noteId = useId();
  const hintId = useId();

  const decisionMutation = useMutation({
    mutationFn: async (action: "approve" | "reject") => {
      if (!question.submittedAt) throw new Error(vi.common.requestFailed);
      if (action === "approve") {
        const { error } = await api.POST("/questions/{id}/approve", {
          params: { path: { id: question.id } },
          body: { submittedAt: question.submittedAt, note: note.trim() || null },
        });
        if (error) throw new Error(getApiErrorMessage(error));
        return action;
      }
      const { error } = await api.POST("/questions/{id}/reject", {
        params: { path: { id: question.id } },
        body: { submittedAt: question.submittedAt, note: note.trim() },
      });
      if (error) throw new Error(getApiErrorMessage(error));
      return action;
    },
    onSuccess: (action) => {
      onSuccess(action === "approve" ? vi.review.approvedSuccess : vi.review.rejectedSuccess);
      void queryClient.invalidateQueries({ queryKey: ["review", "questions"] });
      void queryClient.invalidateQueries({ queryKey: ["questions"] });
      void queryClient.invalidateQueries({ queryKey: ["question", question.id] });
    },
    onError: (err: unknown) => {
      onError(getUiErrorMessage(err));
      // Context A6: re-read the current submission round and queue after a stale-round Conflict or state error.
      void queryClient.invalidateQueries({ queryKey: ["question", question.id] });
      void queryClient.invalidateQueries({ queryKey: ["questions"] });
      void queryClient.invalidateQueries({ queryKey: ["review", "questions"] });
    },
  });

  const allChecked = checks.every(Boolean);

  return (
    <Panel title={vi.review.checklistTitle}>
      <div className="space-y-2.5">
        {vi.review.checklistItems.map((label, idx) => (
          <div key={idx}>
            <Checkbox
              className="flex! items-start!"
              label={label}
              checked={checks[idx]}
              onChange={(e) =>
                setChecks((prev) => prev.map((v, i) => (i === idx ? e.target.checked : v)))
              }
            />
          </div>
        ))}
      </div>

      <hr className="my-3.5 border-line" />

      <Field label={vi.review.noteLabel} htmlFor={noteId}>
        <Textarea
          id={noteId}
          rows={3}
          placeholder={vi.review.notePlaceholder}
          value={note}
          onChange={(e) => setNote(e.target.value)}
        />
      </Field>

      <div className="flex flex-wrap gap-2">
        <Button
          variant="approve"
          disabled={decisionMutation.isPending || !allChecked}
          aria-describedby={allChecked ? undefined : hintId}
          onClick={() => {
            if (!allChecked) {
              onError(vi.review.checklistRequired);
              return;
            }
            decisionMutation.mutate("approve");
          }}
        >
          {vi.review.approve}
        </Button>
        <Button
          variant="danger"
          disabled={decisionMutation.isPending}
          onClick={() => {
            if (!note.trim()) {
              onError(vi.review.rejectNeedsNote);
              return;
            }
            decisionMutation.mutate("reject");
          }}
        >
          {vi.review.reject}
        </Button>
      </div>
      {!allChecked && (
        <p id={hintId} className="mt-2.5 text-xs text-muted">
          {vi.review.checklistRequired}
        </p>
      )}
    </Panel>
  );
}

function ReviewQuestionInner() {
  const id = useSearchParams().get("id");
  const { me } = useAuth();
  const [feedback, setFeedback] = useState<string | null>(null);
  const [errorMsg, setErrorMsg] = useState<string | null>(null);

  const canReview = me?.role === "head" || me?.role === "admin";

  const questionQuery = useQuery({
    queryKey: ["question", id],
    enabled: Boolean(id) && canReview,
    queryFn: async () => {
      const { data, error } = await api.GET("/questions/{id}", { params: { path: { id: id! } } });
      if (error) throw new Error(getApiErrorMessage(error));
      return data;
    },
  });

  const q = questionQuery.data;

  const topicsQuery = useQuery({
    queryKey: ["catalog", "topics", q?.subjectId, q?.grade],
    enabled: Boolean(q?.subjectId) && canReview,
    queryFn: () => fetchAllTopics(q!.subjectId, q!.grade),
  });

  if (!canReview) {
    return (
      <>
        <PageHeader crumb={CRUMB} title={vi.review.questionTitle} />
        <div className={BODY}>
          <Notice tone="error">{vi.common.forbidden}</Notice>
        </div>
      </>
    );
  }
  if (!id) {
    return (
      <>
        <PageHeader crumb={CRUMB} title={vi.review.questionTitle} />
        <div className={BODY}>
          <Notice tone="error">{vi.review.missingId}</Notice>
        </div>
      </>
    );
  }

  const topicName = q ? topicsQuery.data?.find((t) => t.id === q.topicId)?.name : undefined;
  const isSelfAuthor = Boolean(q && me?.id === q.createdBy.id);
  const doubtful = q?.source === "AI" && q.aiVerification === "Disagree";

  return (
    <>
      <PageHeader
        crumb={CRUMB}
        title={vi.review.questionTitle}
        description={q ? vi.review.sentBy(q.createdBy.fullName, formatSentAt(q.submittedAt)) : undefined}
      />
      <div className={BODY}>
        {questionQuery.isLoading && <EmptyState busy title={vi.common.loading} />}
        {questionQuery.isError && <Notice tone="error">{getUiErrorMessage(questionQuery.error)}</Notice>}
        {feedback && (
          <Notice tone="success" className="mb-4">
            {feedback}
          </Notice>
        )}
        {errorMsg && (
          <Notice tone="error" className="mb-4">
            {errorMsg}
          </Notice>
        )}

        {q && (
          <div className="grid gap-5 lg:grid-cols-[minmax(0,1fr)_340px]">
            <Panel className={doubtful ? "min-w-0 border-l-4 border-l-rej" : "min-w-0"}>
              <div className="mb-3.5 flex flex-wrap items-center gap-1.5">
                <StatusText tone={questionTone(q.status)} className="mr-1.5">
                  {vi.questions.statuses[q.status]}
                </StatusText>
                <Tag>{vi.questions.types[q.type]}</Tag>
                <Tag>{vi.questions.difficulties[q.difficulty]}</Tag>
                <Tag>{`${vi.common.gradePrefix} ${q.grade}`}</Tag>
                {topicName && <Tag>{`${vi.questions.topic}: ${topicName}`}</Tag>}
                {q.source === "AI" && (
                  <>
                    <Tag tone="ai">{vi.questions.aiSource}</Tag>
                    <Tag tone={AI_FLAG_TONE[q.aiVerification]}>{vi.questions.aiFlags[q.aiVerification]}</Tag>
                    <Tag>{q.aiReviewedByAuthor ? vi.questions.aiReviewed : vi.questions.aiNotReviewed}</Tag>
                  </>
                )}
              </div>

              {q.aiVerificationNote && (
                <Notice tone="warn" title={vi.questions.aiVerification} className="mb-3.5">
                  {q.aiVerificationNote}
                </Notice>
              )}

              <div className="text-[14.5px] break-words [&_.katex-display]:max-w-full [&_.katex-display]:overflow-x-auto">
                <MathMarkdown>{q.stem}</MathMarkdown>
              </div>

              <div className="mt-3.5">
                <QuestionPayloadView type={q.type} payload={q.payload} />
              </div>

              {q.explanation && (
                <div className="mt-3.5 rounded-md bg-tint px-3 py-2.5 text-[13.5px]">
                  <p className="font-semibold text-ink-soft">{vi.questions.explanation}</p>
                  <div className="mt-1">
                    <MathMarkdown>{q.explanation}</MathMarkdown>
                  </div>
                </div>
              )}
            </Panel>

            {q.status === "Submitted" && (
              <aside className="min-w-0 lg:sticky lg:top-4 lg:self-start">
                {isSelfAuthor ? (
                  <Notice tone="warn">{vi.review.selfReviewNotice}</Notice>
                ) : (
                  <QuestionDecisionForm
                    key={`${q.id}-${q.submittedAt ?? ""}-${q.status}`}
                    question={q}
                    onSuccess={(msg) => {
                      setErrorMsg(null);
                      setFeedback(msg);
                    }}
                    onError={(msg) => {
                      setFeedback(null);
                      setErrorMsg(msg);
                    }}
                  />
                )}
              </aside>
            )}
          </div>
        )}
      </div>
    </>
  );
}

export default function ReviewQuestionPage() {
  return (
    <Suspense
      fallback={
        <>
          <PageHeader crumb={CRUMB} title={vi.review.questionTitle} />
          <div className={BODY}>
            <EmptyState busy title={vi.common.loading} />
          </div>
        </>
      }
    >
      <ReviewQuestionInner />
    </Suspense>
  );
}
