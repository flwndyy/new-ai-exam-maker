"use client";

import { useMutation, useQueryClient } from "@tanstack/react-query";
import Link from "next/link";

import { MathMarkdown } from "@/components/math-markdown";
import { QuestionPayloadView } from "@/components/questions/question-payload-view";
import { api } from "@/lib/api/client";
import type { components } from "@/lib/api/schema";
import { vi } from "@/messages/vi";

type Question = components["schemas"]["Question"];

/** Same rule as POST /questions/{id}/mark-reviewed: author, AI source, not yet reviewed, Draft/Rejected. */
export function canMarkReviewed(question: Question, meId: string): boolean {
  return (
    question.createdBy.id === meId &&
    question.source === "AI" &&
    !question.aiReviewedByAuthor &&
    (question.status === "Draft" || question.status === "Rejected")
  );
}

/** Shared with the question detail/review pages so edits there and reviews here see the same entry. */
export const questionKey = (id: string) => ["question", id] as const;

/** The question was deleted or is no longer visible (404/403); not retried. */
export class QuestionUnavailableError extends Error {}

const FLAG_CLASS: Record<Question["aiVerification"], string> = {
  Agree: "bg-emerald-100 text-emerald-800",
  Disagree: "bg-red-100 text-red-800",
  NotChecked: "bg-slate-100 text-slate-700",
};

/** One generated question on the job page; AI content is rendered only through sanitized MathMarkdown. */
export function GeneratedQuestionCard({
  question,
  index,
  meId,
  refreshFailed = false,
  refreshing = false,
  onRetry,
}: {
  question: Question | null;
  index: number;
  meId: string;
  /** The cached copy could not be reloaded: it may be stale, so reviewing it is blocked until a reload succeeds. */
  refreshFailed?: boolean;
  refreshing?: boolean;
  onRetry?: () => void;
}) {
  const queryClient = useQueryClient();
  const markReviewed = useMutation({
    mutationFn: async (id: string) => {
      const { data, error } = await api.POST("/questions/{id}/mark-reviewed", { params: { path: { id } } });
      if (error || !data) throw new Error(vi.ai.reviewFailed);
      return data;
    },
    onSuccess: (updated) => {
      queryClient.setQueryData(questionKey(updated.id), updated);
      void queryClient.invalidateQueries({ queryKey: ["questions"] });
    },
  });

  const label = vi.ai.questionLabel(index);
  if (!question) {
    return (
      <article className="rounded border border-slate-200 bg-slate-50 p-4 text-sm text-slate-600">
        <h3 className="font-semibold text-slate-900">{label}</h3>
        <p>{vi.ai.questionUnavailable}</p>
      </article>
    );
  }

  const flag = question.aiVerification;
  return (
    <article className="space-y-3 rounded border border-slate-200 bg-white p-4">
      <header className="flex flex-wrap items-center gap-2">
        <h3 className="font-semibold text-slate-900">{label}</h3>
        <span className={`rounded px-2 py-0.5 text-xs font-medium ${FLAG_CLASS[flag]}`}>
          {flag === "Disagree"
            ? `⚠ ${vi.questions.aiFlags.Disagree} — ${vi.ai.disagreeWarning}`
            : vi.questions.aiFlags[flag]}
        </span>
        <span className="text-xs text-slate-500">
          {vi.common.status}: {vi.questions.statuses[question.status]}
        </span>
      </header>

      {refreshFailed && (
        <div role="alert" className="flex flex-wrap items-center gap-3 rounded border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-800">
          <span>{vi.ai.questionRefreshFailed}</span>
          {onRetry && (
            <button type="button" disabled={refreshing} onClick={onRetry} className="underline disabled:opacity-50">
              {vi.ai.retry}
            </button>
          )}
        </div>
      )}

      <div className="text-sm text-slate-900">
        <MathMarkdown>{question.stem}</MathMarkdown>
      </div>
      <QuestionPayloadView type={question.type} payload={question.payload} />

      {question.explanation && (
        <div className="text-sm text-slate-700">
          <p className="font-medium">{vi.ai.explanation}:</p>
          <MathMarkdown>{question.explanation}</MathMarkdown>
        </div>
      )}
      {question.aiVerificationNote && (
        <p className="text-sm text-slate-600">
          <span className="font-medium">{vi.ai.verificationNote}:</span> <span>{question.aiVerificationNote}</span>
        </p>
      )}

      <footer className="flex flex-wrap items-center justify-end gap-2">
        {markReviewed.isError && <span className="text-sm text-red-700">{vi.ai.reviewFailed}</span>}
        {question.source === "AI" && question.aiReviewedByAuthor && (
          <span className="text-sm text-emerald-700">{vi.ai.reviewed}</span>
        )}
        <Link
          href={`/questions/detail/?id=${question.id}`}
          className="rounded border border-slate-300 px-3 py-1.5 text-sm text-slate-700 hover:bg-slate-50"
        >
          {vi.common.edit}
        </Link>
        {!refreshFailed && canMarkReviewed(question, meId) && (
          <button
            type="button"
            disabled={markReviewed.isPending || refreshing}
            onClick={() => markReviewed.mutate(question.id)}
            className="rounded border border-amber-400 bg-amber-50 px-3 py-1.5 text-sm text-amber-900 hover:bg-amber-100 disabled:opacity-50"
          >
            {vi.questions.markReviewed}
          </button>
        )}
      </footer>
    </article>
  );
}
