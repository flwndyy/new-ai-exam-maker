"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Suspense, useState } from "react";

import { MathMarkdown } from "@/components/math-markdown";
import { QuestionPayloadView } from "@/components/questions/question-payload-view";
import { api } from "@/lib/api/client";
import { getApiErrorMessage } from "@/lib/api/error";
import { useAuth } from "@/lib/auth/auth-context";
import { vi } from "@/messages/vi";

function ReviewQuestionInner() {
  const id = useSearchParams().get("id");
  const { me } = useAuth();
  const queryClient = useQueryClient();

  const [checks, setChecks] = useState<boolean[]>(vi.review.checklistItems.map(() => false));
  const [note, setNote] = useState("");
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

  const decisionMutation = useMutation({
    mutationFn: async (action: "approve" | "reject") => {
      const q = questionQuery.data;
      if (!q?.submittedAt) throw new Error(vi.common.requestFailed);
      if (action === "approve") {
        const { error } = await api.POST("/questions/{id}/approve", {
          params: { path: { id: q.id } },
          body: { submittedAt: q.submittedAt, note: note.trim() || null },
        });
        if (error) throw new Error(getApiErrorMessage(error));
        return action;
      }
      const { error } = await api.POST("/questions/{id}/reject", {
        params: { path: { id: q.id } },
        body: { submittedAt: q.submittedAt, note: note.trim() },
      });
      if (error) throw new Error(getApiErrorMessage(error));
      return action;
    },
    onSuccess: (action) => {
      setErrorMsg(null);
      setFeedback(action === "approve" ? vi.review.approvedSuccess : vi.review.rejectedSuccess);
      void queryClient.invalidateQueries({ queryKey: ["review", "questions"] });
      void queryClient.invalidateQueries({ queryKey: ["question", id] });
    },
    onError: (err: Error) => {
      setFeedback(null);
      setErrorMsg(err.message);
    },
  });

  if (!canReview) {
    return <p className="rounded border border-rose-200 bg-rose-50 p-4 text-sm text-rose-800">{vi.common.forbidden}</p>;
  }
  if (!id) return <p className="text-sm text-rose-600">{vi.review.missingId}</p>;

  const q = questionQuery.data;
  const allChecked = checks.every(Boolean);

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
          <div className="rounded border border-slate-200 bg-white p-5 space-y-4">
            <div className="flex flex-wrap items-center gap-2 text-xs">
              <span className="rounded bg-slate-100 px-2.5 py-1 font-medium">
                {vi.questions.statuses[q.status]}
              </span>
              <span className="rounded bg-blue-50 px-2.5 py-1 text-blue-800">
                {vi.questions.types[q.type]} · {vi.common.gradePrefix} {q.grade}
              </span>
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
                      setErrorMsg(vi.review.checklistRequired);
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
                      setErrorMsg(vi.review.rejectNeedsNote);
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
