"use client";

import { MathMarkdown } from "@/components/math-markdown";
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
              className={`rounded border px-3 py-2 text-sm ${
                isCorrect ? "border-emerald-500 bg-emerald-50" : "border-slate-200 bg-white"
              }`}
            >
              <span className="mr-2 font-semibold">{opt.label}.</span>
              <span className="inline-block align-top">
                <MathMarkdown>{opt.text}</MathMarkdown>
              </span>
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
          <li key={stmt.label} className="flex items-start justify-between gap-4 rounded border border-slate-200 bg-white px-3 py-2 text-sm">
            <div>
              <span className="mr-2 font-semibold">{stmt.label})</span>
              <span className="inline-block align-top">
                <MathMarkdown>{stmt.text}</MathMarkdown>
              </span>
            </div>
            <span className={`shrink-0 rounded px-2 py-0.5 text-xs font-medium ${stmt.isTrue ? "bg-emerald-100 text-emerald-800" : "bg-rose-100 text-rose-800"}`}>
              {stmt.isTrue ? vi.questions.trueLabel : vi.questions.falseLabel}
            </span>
          </li>
        ))}
      </ul>
    );
  }

  if (type === "ShortAnswer" && "acceptedAnswers" in payload) {
    return (
      <div className="rounded border border-slate-200 bg-white p-3 text-sm">
        <p className="font-medium text-slate-700">{vi.questions.shortAnswerValue}:</p>
        <p className="mt-1 font-mono text-emerald-700">
          {payload.acceptedAnswers.join(" | ")}
          {payload.unit ? ` (${payload.unit})` : ""}
        </p>
        {payload.tolerance != null && (
          <p className="mt-1 text-xs text-slate-600">
            {vi.questions.shortTolerance}: ±{formatDecimalForInput(payload.tolerance)}
          </p>
        )}
      </div>
    );
  }

  if (type === "Essay" && "rubric" in payload) {
    return (
      <div className="space-y-2 rounded border border-slate-200 bg-white p-3 text-sm">
        <div>
          <p className="font-medium text-slate-700">{vi.questions.modelAnswer}:</p>
          <div className="mt-1 text-slate-800">
            <MathMarkdown>{payload.modelAnswer}</MathMarkdown>
          </div>
        </div>
        <div className="border-t border-slate-100 pt-2">
          <p className="font-medium text-slate-700">{vi.questions.essayRubric}:</p>
          <ul className="mt-1 divide-y divide-slate-100">
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
