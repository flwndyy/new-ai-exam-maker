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
import { MetaBar } from "@/components/ui/meta-bar";
import { Notice } from "@/components/ui/notice";
import { Panel } from "@/components/ui/panel";
import { StatusText } from "@/components/ui/status-text";
import { api } from "@/lib/api/client";
import { getApiErrorMessage, getUiErrorMessage } from "@/lib/api/error";
import type { components } from "@/lib/api/schema";
import { useAuth } from "@/lib/auth/auth-context";
import { formatDecimalForInput } from "@/lib/format/decimal";
import { examVersionTone } from "@/lib/ui/status";
import { vi } from "@/messages/vi";

type ExamVersion = components["schemas"]["ExamVersion"];
type ExamAction = "approve" | "reject" | "publish" | "unpublish" | "archive";

const BODY = "px-4 py-5 sm:px-7 sm:py-6";
const CRUMB = { href: "/review/", label: vi.review.title };

/** Bind checklist and note state to one submission round via parent key (`id + submittedAt + status`). */
function ExamDecisionPanel({
  isSelfAuthor,
  pending,
  onDecide,
  onError,
}: {
  isSelfAuthor: boolean;
  pending: boolean;
  onDecide: (action: "approve" | "reject", note: string) => void;
  onError: (message: string) => void;
}) {
  const [checks, setChecks] = useState<boolean[]>(vi.review.checklistItems.map(() => false));
  const [note, setNote] = useState("");
  const noteId = useId();
  const hintId = useId();

  if (isSelfAuthor) {
    return <Notice tone="warn">{vi.review.selfReviewNotice}</Notice>;
  }

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
                setChecks((prev) => prev.map((val, i) => (i === idx ? e.target.checked : val)))
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
          disabled={pending || !allChecked}
          aria-describedby={allChecked ? undefined : hintId}
          onClick={() => {
            if (!allChecked) {
              onError(vi.review.checklistRequired);
              return;
            }
            onDecide("approve", note);
          }}
        >
          {vi.review.approve}
        </Button>
        <Button
          variant="danger"
          disabled={pending}
          onClick={() => {
            if (!note.trim()) {
              onError(vi.review.rejectNeedsNote);
              return;
            }
            onDecide("reject", note);
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

function ReviewExamInner() {
  const vid = useSearchParams().get("id");
  const { me } = useAuth();
  const queryClient = useQueryClient();
  const [feedback, setFeedback] = useState<string | null>(null);
  const [errorMsg, setErrorMsg] = useState<string | null>(null);

  const canReview = me?.role === "head" || me?.role === "admin";

  const versionQuery = useQuery({
    queryKey: ["exam-version", vid],
    enabled: Boolean(vid) && canReview,
    queryFn: async () => {
      const { data, error } = await api.GET("/exam-versions/{vid}", { params: { path: { vid: vid! } } });
      if (error) throw new Error(getApiErrorMessage(error));
      return data;
    },
  });

  const v: ExamVersion | undefined = versionQuery.data;

  const actionMutation = useMutation({
    mutationFn: async ({ action, note }: { action: ExamAction; note?: string }) => {
      if (!v) throw new Error(vi.common.requestFailed);
      if (action === "approve") {
        const { error } = await api.POST("/exam-versions/{vid}/approve", {
          params: { path: { vid: v.id } },
          body: { submittedAt: v.submittedAt!, note: note?.trim() || null },
        });
        if (error) throw new Error(getApiErrorMessage(error));
        return action;
      }
      if (action === "reject") {
        const { error } = await api.POST("/exam-versions/{vid}/reject", {
          params: { path: { vid: v.id } },
          body: { submittedAt: v.submittedAt!, note: (note ?? "").trim() },
        });
        if (error) throw new Error(getApiErrorMessage(error));
        return action;
      }
      const pathMap = {
        publish: "/exam-versions/{vid}/publish",
        unpublish: "/exam-versions/{vid}/unpublish",
        archive: "/exam-versions/{vid}/archive",
      } as const;
      const { error } = await api.POST(pathMap[action], { params: { path: { vid: v.id } } });
      if (error) throw new Error(getApiErrorMessage(error));
      return action;
    },
    onSuccess: (action) => {
      const messageMap = {
        approve: vi.review.approvedSuccess,
        reject: vi.review.rejectedSuccess,
        publish: vi.review.publishedSuccess,
        unpublish: vi.review.unpublishedSuccess,
        archive: vi.review.archivedSuccess,
      } as const;
      setErrorMsg(null);
      setFeedback(messageMap[action]);
      void queryClient.invalidateQueries({ queryKey: ["review", "exams"] });
      void queryClient.invalidateQueries({ queryKey: ["exams"] });
      void queryClient.invalidateQueries({ queryKey: ["exam-versions"] });
      void queryClient.invalidateQueries({ queryKey: ["exam-version", vid] });
    },
    onError: (err: unknown) => {
      setFeedback(null);
      setErrorMsg(getUiErrorMessage(err));
      // Re-read the exam version and review queue after a stale-round Conflict or concurrent state transition.
      void queryClient.invalidateQueries({ queryKey: ["exam-version", vid] });
      void queryClient.invalidateQueries({ queryKey: ["exams"] });
      void queryClient.invalidateQueries({ queryKey: ["exam-versions"] });
      void queryClient.invalidateQueries({ queryKey: ["review", "exams"] });
    },
  });

  if (!canReview) {
    return (
      <>
        <PageHeader crumb={CRUMB} title={vi.review.examTitle} />
        <div className={BODY}>
          <Notice tone="error">{vi.common.forbidden}</Notice>
        </div>
      </>
    );
  }
  if (!vid) {
    return (
      <>
        <PageHeader crumb={CRUMB} title={vi.review.examTitle} />
        <div className={BODY}>
          <Notice tone="error">{vi.review.missingId}</Notice>
        </div>
      </>
    );
  }

  const isAdmin = me?.role === "admin";
  const busy = actionMutation.isPending;
  const canArchive = v?.status === "Approved" || v?.status === "Rejected";
  const canPublish = isAdmin && v?.status === "Approved";
  const canUnpublish = isAdmin && v?.status === "Published";
  const warnings = v?.warnings ?? [];

  const boardActions =
    v && (canArchive || canPublish || canUnpublish) ? (
      <>
        {canArchive && (
          <Button
            variant="boardSecondary"
            disabled={busy}
            onClick={() => {
              if (window.confirm(vi.review.confirmArchiveExam)) {
                actionMutation.mutate({ action: "archive" });
              }
            }}
          >
            {vi.review.archive}
          </Button>
        )}
        {canUnpublish && (
          <Button
            variant="boardSecondary"
            disabled={busy}
            onClick={() => {
              if (window.confirm(vi.review.confirmUnpublishExam)) {
                actionMutation.mutate({ action: "unpublish" });
              }
            }}
          >
            {vi.review.unpublish}
          </Button>
        )}
        {canPublish && (
          <Button
            variant="boardPrimary"
            disabled={busy}
            onClick={() => {
              if (window.confirm(vi.review.confirmPublishExam)) {
                actionMutation.mutate({ action: "publish" });
              }
            }}
          >
            {vi.review.publish}
          </Button>
        )}
      </>
    ) : undefined;

  return (
    <>
      <PageHeader
        crumb={CRUMB}
        title={v ? v.examTitle : vi.review.examTitle}
        description={v ? vi.review.examSubtitle(v.versionNo, v.createdBy.fullName) : undefined}
        actions={boardActions}
      />
      <div className={BODY}>
        {versionQuery.isLoading && <EmptyState busy title={vi.common.loading} />}
        {versionQuery.isError && <Notice tone="error">{getUiErrorMessage(versionQuery.error)}</Notice>}
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

        {v && (
          <>
            <MetaBar
              items={[
                {
                  label: vi.common.status,
                  value: (
                    <StatusText tone={examVersionTone(v.status)} className="text-[17px]!">
                      {vi.review.examStatuses[v.status]}
                    </StatusText>
                  ),
                },
                { label: vi.common.gradePrefix, value: v.grade },
                { label: vi.review.examDuration, value: v.durationMinutes },
                { label: vi.review.examQuestionCount, value: v.questionCount },
                { label: vi.review.examTotalPoints, value: formatDecimalForInput(v.totalPoints) },
              ]}
            />

            {warnings.length > 0 && (
              <Notice tone="warn" title={vi.review.warningsTitle} className="mb-4">
                <ul className="mt-1 list-disc pl-4">
                  {warnings.map((w, idx) => (
                    <li key={idx}>{w.message}</li>
                  ))}
                </ul>
              </Notice>
            )}

            <div className={v.status === "Submitted" ? "grid gap-5 lg:grid-cols-[minmax(0,1fr)_340px]" : ""}>
              <ol className="min-w-0">
                {v.items.map((item) => (
                  <li
                    key={item.sourceQuestionId}
                    className="mb-2 min-w-0 rounded-md bg-white px-4 py-3.5 ring-1 ring-line"
                  >
                    <div className="mb-2 flex items-baseline justify-between gap-3 text-[12.5px] text-muted">
                      <span className="font-semibold text-ink-soft">
                        {vi.review.questionItemPrefix} {item.sortOrder}
                        {item.difficulty ? `, ${vi.questions.difficulties[item.difficulty]}` : ""}
                      </span>
                      <span className="font-semibold whitespace-nowrap text-ink-soft tabular-nums">
                        {formatDecimalForInput(item.points)} {vi.review.points}
                      </span>
                    </div>
                    {item.stem && (
                      <div className="text-[14.5px] break-words [&_.katex-display]:max-w-full [&_.katex-display]:overflow-x-auto">
                        <MathMarkdown>{item.stem}</MathMarkdown>
                      </div>
                    )}
                    {item.type && item.payload && (
                      <div className="mt-2.5">
                        <QuestionPayloadView type={item.type} payload={item.payload} />
                      </div>
                    )}
                    {item.explanation && (
                      <div className="mt-2.5 rounded-md bg-tint px-3 py-2.5 text-[13.5px]">
                        <p className="font-semibold text-ink-soft">{vi.questions.explanation}</p>
                        <div className="mt-1">
                          <MathMarkdown>{item.explanation}</MathMarkdown>
                        </div>
                      </div>
                    )}
                  </li>
                ))}
              </ol>

              {v.status === "Submitted" && (
                <aside className="min-w-0 lg:sticky lg:top-4 lg:self-start">
                  <ExamDecisionPanel
                    key={`${v.id}-${v.submittedAt ?? ""}-${v.status}`}
                    isSelfAuthor={me?.id === v.createdBy.id}
                    pending={busy}
                    onDecide={(action, note) => actionMutation.mutate({ action, note })}
                    onError={(msg) => {
                      setFeedback(null);
                      setErrorMsg(msg);
                    }}
                  />
                </aside>
              )}
            </div>
          </>
        )}
      </div>
    </>
  );
}

export default function ReviewExamPage() {
  return (
    <Suspense
      fallback={
        <>
          <PageHeader crumb={CRUMB} title={vi.review.examTitle} />
          <div className={BODY}>
            <EmptyState busy title={vi.common.loading} />
          </div>
        </>
      }
    >
      <ReviewExamInner />
    </Suspense>
  );
}
