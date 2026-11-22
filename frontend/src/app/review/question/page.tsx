"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Suspense, useState } from "react";

import { MathMarkdown } from "@/components/math-markdown";
import { QuestionPayloadView } from "@/components/questions/question-payload-view";
import { fetchAllTopics } from "@/lib/api/catalog";
import { api } from "@/lib/api/client";
import { getApiErrorMessage } from "@/lib/api/error";
import type { components } from "@/lib/api/schema";
import { useAuth } from "@/lib/auth/auth-context";
import { vi } from "@/messages/vi";

type Question = components["schemas"]["Question"];

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
      void queryClient.invalidateQueries({ queryKey: ["question", question.id] });
    },
    onError: (err: Error) => {
      onError(err.message);
      // Context A6: re-read the current submission round and queue after a stale-round Conflict or state error.
      void queryClient.invalidateQueries({ queryKey: ["question", question.id] });
      void queryClient.invalidateQueries({ queryKey: ["review", "questions"] });
    },
  });

  const allChecked = checks.every(Boolean);

  return (
    <div className="space-y-4 rounded border border-slate-200 bg-white p-5">
      <h2 className="text-sm font-semibold text-slate-900">{vi.review.checklistTitle}</h2>
      <div className="space-y-2">
        {vi.review.checklistItems.map((label, idx) => (
          <label key={idx} className="flex items-center gap-2 text-sm text-slate-700">
            <input
              type="checkbox"
              checked={checks[idx]}
              onChange={(e) =>
                setChecks((prev) => prev.map((v, i) => (i === idx ? e.target.checked : v)))
              }
            />
            {label}
          </label>
        ))}
      </div>

      <label className="block text-sm">
        <span className="font-medium text-slate-700">{vi.review.noteLabel}</span>
        <textarea
          rows={3}
          placeholder={vi.review.notePlaceholder}
          value={note}
          onChange={(e) => setNote(e.target.value)}
          className="mt-1 w-full rounded border border-slate-300 p-2.5 text-sm"
        />
      </label>

      <div className="flex flex-wrap gap-3">
        <button
          type="button"
          disabled={decisionMutation.isPending}
          onClick={() => {
            if (!allChecked) {
              onError(vi.review.checklistRequired);
              return;
            }
            decisionMutation.mutate("approve");
          }}
          className="rounded bg-emerald-600 px-4 py-2 text-sm font-medium text-white hover:bg-emerald-700 disabled:opacity-50"
        >
          {vi.review.approve}
        </button>
        <button
          type="button"
          disabled={decisionMutation.isPending}
          onClick={() => {
            if (!note.trim()) {
              onError(vi.review.rejectNeedsNote);
              return;
            }
            decisionMutation.mutate("reject");
          }}
          className="rounded bg-rose-600 px-4 py-2 text-sm font-medium text-white hover:bg-rose-700 disabled:opacity-50"
        >
          {vi.review.reject}
        </button>
      </div>
    </div>
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
    return <p className="rounded border border-rose-200 bg-rose-50 p-4 text-sm text-rose-800">{vi.common.forbidden}</p>;
  }
  if (!id) return <p className="text-sm text-rose-600">{vi.review.missingId}</p>;

  const topicName = q ? topicsQuery.data?.find((t) => t.id === q.topicId)?.name : undefined;
  const isSelfAuthor = Boolean(q && me?.id === q.createdBy.id);

  return (
    <section className="space-y-6">
      <div>
        <Link href="/review/" className="text-sm text-slate-600 hover:underline">
          ← {vi.common.back}
        </Link>
        <h1 className="mt-1 text-2xl font-bold text-slate-900">{vi.review.questionTitle}</h1>
      </div>

      {questionQuery.isLoading && <p className="text-sm text-slate-600">{vi.common.loading}</p>}
      {questionQuery.isError && <p className="text-sm text-rose-600">{questionQuery.error.message}</p>}
      {feedback && <p className="rounded border border-emerald-300 bg-emerald-50 p-3 text-sm text-emerald-800">{feedback}</p>}
      {errorMsg && <p className="rounded border border-rose-300 bg-rose-50 p-3 text-sm text-rose-800">{errorMsg}</p>}

      {q && (
        <div className="space-y-5">
          <div className="space-y-4 rounded border border-slate-200 bg-white p-5">
            <div className="flex flex-wrap items-center gap-2 text-xs">
              <span className="rounded bg-slate-100 px-2.5 py-1 font-medium">
                {vi.questions.statuses[q.status]}
              </span>
              <span className="rounded bg-blue-50 px-2.5 py-1 text-blue-800">
                {vi.questions.types[q.type]} · {vi.common.gradePrefix} {q.grade} · {vi.questions.difficulties[q.difficulty]}
              </span>
              {topicName && (
                <span className="rounded bg-slate-100 px-2.5 py-1 text-slate-700">
                  {vi.questions.topic}: {topicName}
                </span>
              )}
              <span className="rounded bg-amber-50 px-2.5 py-1 text-amber-800">
                {vi.questions.aiFlags[q.aiVerification]}
                {q.source === "AI" ? ` · ${q.aiReviewedByAuthor ? vi.questions.aiReviewed : vi.questions.aiNotReviewed}` : ""}
              </span>
            </div>

            {q.aiVerificationNote && (
              <p className="rounded border border-amber-200 bg-amber-50 p-2.5 text-xs text-amber-900">
                <strong>{vi.questions.aiVerification}:</strong> {q.aiVerificationNote}
              </p>
            )}

            <div className="prose prose-sm max-w-none text-slate-900">
              <MathMarkdown>{q.stem}</MathMarkdown>
            </div>

            <QuestionPayloadView type={q.type} payload={q.payload} />

            {q.explanation && (
              <div className="rounded border border-slate-200 bg-slate-50 p-3 text-sm">
                <p className="font-medium text-slate-700">{vi.questions.explanation}:</p>
                <div className="mt-1">
                  <MathMarkdown>{q.explanation}</MathMarkdown>
                </div>
              </div>
            )}
          </div>

          {q.status === "Submitted" && (
            isSelfAuthor ? (
              <p className="rounded border border-amber-300 bg-amber-50 p-4 text-sm text-amber-900">
                {vi.review.selfReviewNotice}
              </p>
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
            )
          )}
        </div>
      )}
    </section>
  );
}

export default function ReviewQuestionPage() {
  return (
    <Suspense fallback={<p>{vi.common.loading}</p>}>
      <ReviewQuestionInner />
    </Suspense>
  );
}
