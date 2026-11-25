"use client";

import { useMutation, useQueryClient } from "@tanstack/react-query";

import { MathMarkdown } from "@/components/math-markdown";
import { Button, ButtonLink } from "@/components/ui/button";
import { Notice } from "@/components/ui/notice";
import { StatusText } from "@/components/ui/status-text";
import { Tag, type TagTone } from "@/components/ui/tag";
import { QuestionPayloadView } from "@/components/questions/question-payload-view";
import { api } from "@/lib/api/client";
import type { components } from "@/lib/api/schema";
import { aiVerificationTone, questionTone } from "@/lib/ui/status";
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

const FLAG_TONE: Record<Question["aiVerification"], TagTone> = {
  Agree: aiVerificationTone("Agree"),
  Disagree: aiVerificationTone("Disagree"),
  NotChecked: aiVerificationTone("NotChecked"),
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
      <article className="rounded-lg border border-line bg-tint p-4 text-[13.5px] text-ink-soft">
        <h3 className="font-semibold text-ink">{label}</h3>
        <p className="mt-1">{vi.ai.questionUnavailable}</p>
      </article>
    );
  }

  const flag = question.aiVerification;
  const disagree = flag === "Disagree";
  return (
    <article
      className={[
        "space-y-3 rounded-lg border bg-surface p-4",
        disagree ? "border-rej border-l-4" : "border-line",
      ].join(" ")}
    >
      <header className="flex flex-wrap items-center gap-x-2.5 gap-y-1.5">
        <h3 className="text-[15px] font-semibold">{label}</h3>
        <Tag tone={FLAG_TONE[flag]}>
          {disagree ? `${vi.questions.aiFlags.Disagree}, ${vi.ai.disagreeWarning}` : vi.questions.aiFlags[flag]}
        </Tag>
        <StatusText tone={questionTone(question.status)} className="ml-auto">
          {vi.questions.statuses[question.status]}
        </StatusText>
      </header>

      {refreshFailed && (
        <Notice
          tone="error"
          action={
            onRetry && (
              <Button variant="secondary" size="sm" disabled={refreshing} onClick={onRetry}>
                {vi.ai.retry}
              </Button>
            )
          }
        >
          {vi.ai.questionRefreshFailed}
        </Notice>
      )}

      <div className="text-[14px] text-ink">
        <MathMarkdown>{question.stem}</MathMarkdown>
      </div>
      <QuestionPayloadView type={question.type} payload={question.payload} />

      {question.explanation && (
        <div className="rounded-md bg-tint px-3 py-2.5 text-[13.5px] text-ink">
          <p className="font-semibold text-ink-soft">{vi.ai.explanation}</p>
          <MathMarkdown>{question.explanation}</MathMarkdown>
        </div>
      )}
      {question.aiVerificationNote && (
        <p className="text-[13px] text-ink-soft">
          <span className="font-semibold">{vi.ai.verificationNote}:</span> <span>{question.aiVerificationNote}</span>
        </p>
      )}

      <footer className="flex flex-wrap items-center justify-end gap-2">
        {markReviewed.isError && <span className="text-[13px] text-rej">{vi.ai.reviewFailed}</span>}
        {question.source === "AI" && question.aiReviewedByAuthor && (
          <StatusText tone="ok" className="mr-auto">
            {vi.ai.reviewed}
          </StatusText>
        )}
        <ButtonLink href={`/questions/detail/?id=${question.id}`} variant="secondary" size="sm">
          {vi.common.edit}
        </ButtonLink>
        {!refreshFailed && canMarkReviewed(question, meId) && (
          <Button
            size="sm"
            disabled={markReviewed.isPending || refreshing}
            onClick={() => markReviewed.mutate(question.id)}
          >
            {vi.questions.markReviewed}
          </Button>
        )}
      </footer>
    </article>
  );
}
