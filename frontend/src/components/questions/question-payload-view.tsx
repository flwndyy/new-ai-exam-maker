"use client";

import { MathMarkdown } from "@/components/math-markdown";
import { Tag } from "@/components/ui/tag";
import type { components } from "@/lib/api/schema";
import { formatDecimalForInput } from "@/lib/format/decimal";
import { vi } from "@/messages/vi";

type QuestionType = components["schemas"]["QuestionType"];
type QuestionPayload = components["schemas"]["QuestionPayload"];

/** Render structured answer/rubric payload with sanitized KaTeX/Markdown across detail and review screens. */
export function QuestionPayloadView({ type, payload }: { type: QuestionType; payload: QuestionPayload }) {
  if (type === "MultipleChoice" && "options" in payload) {
    return (
      <ul className="space-y-2">
        {payload.options.map((opt) => {
          const isCorrect = opt.label === payload.correctLabel;
          return (
            <li
              key={opt.label}
              className={`flex gap-2 rounded-md border px-3 py-2 text-[13.5px] text-ink ${
                isCorrect ? "border-ok bg-ok-bg" : "border-line bg-surface"
              }`}
            >
              <span className="shrink-0 font-semibold">{opt.label}.</span>
              {/* min-w-0 bounds the option so a wide display formula scrolls inside it, not the page. */}
              <div className="min-w-0 flex-1">
                {isCorrect && <span className="sr-only">{vi.questions.correctOption}: </span>}
                <MathMarkdown>{opt.text}</MathMarkdown>
              </div>
            </li>
          );
        })}
      </ul>
    );
  }

  if (type === "TrueFalse" && "statements" in payload) {
    return (
      <ul className="space-y-2">
        {payload.statements.map((stmt) => (
          <li key={stmt.label} className="flex items-start justify-between gap-4 rounded-md border border-line bg-surface px-3 py-2 text-[13.5px]">
            <div className="flex min-w-0 flex-1 gap-2">
              <span className="shrink-0 font-semibold">{stmt.label})</span>
              <div className="min-w-0 flex-1">
                <MathMarkdown>{stmt.text}</MathMarkdown>
              </div>
            </div>
            <Tag tone={stmt.isTrue ? "ok" : "rej"} className="shrink-0">
              {stmt.isTrue ? vi.questions.trueLabel : vi.questions.falseLabel}
            </Tag>
          </li>
        ))}
      </ul>
    );
  }

  if (type === "ShortAnswer" && "acceptedAnswers" in payload) {
    return (
      <div className="rounded-md border border-line bg-surface p-3 text-[13.5px]">
        <p className="font-semibold text-ink-soft">{vi.questions.shortAnswerValue}:</p>
        <p className="mt-1 font-mono text-ok">
          {payload.acceptedAnswers.join(" | ")}
          {payload.unit ? ` (${payload.unit})` : ""}
        </p>
        {payload.tolerance != null && (
          <p className="mt-1 text-xs text-muted">
            {vi.questions.shortTolerance}: ±{formatDecimalForInput(payload.tolerance)}
          </p>
        )}
      </div>
    );
  }

  if (type === "Essay" && "rubric" in payload) {
    return (
      <div className="space-y-2 rounded-md border border-line bg-surface p-3 text-[13.5px]">
        <div>
          <p className="font-semibold text-ink-soft">{vi.questions.modelAnswer}:</p>
          <div className="mt-1 text-ink">
            <MathMarkdown>{payload.modelAnswer}</MathMarkdown>
          </div>
        </div>
        <div className="border-t border-line pt-2">
          <p className="font-semibold text-ink-soft">{vi.questions.essayRubric}:</p>
          <ul className="mt-1 divide-y divide-line">
            {payload.rubric.map((item, idx) => (
              <li key={idx} className="flex justify-between gap-4 py-1">
                <span>{item.criterion}</span>
                <span className="shrink-0 font-medium">{formatDecimalForInput(item.points)} {vi.review.points}</span>
              </li>
            ))}
          </ul>
        </div>
      </div>
    );
  }

  return null;
}
