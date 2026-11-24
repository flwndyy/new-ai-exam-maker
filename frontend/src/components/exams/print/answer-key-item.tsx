"use client";

import { MathMarkdown } from "@/components/math-markdown";
import { QuestionHeading } from "@/components/exams/print/student-sheet-item";
import { QuestionPayloadView } from "@/components/questions/question-payload-view";
import type { components } from "@/lib/api/schema";
import { vi } from "@/messages/vi";

type AnswerKeyItem = components["schemas"]["AnswerKeyItem"];

/** Teacher answer block: stem, full payload with answers, difficulty and explanation. */
export function AnswerKeyItemView({ item }: { item: AnswerKeyItem }) {
  const t = vi.exams.print;
  return (
    <article className="print-item space-y-2">
      <div className="flex items-baseline justify-between gap-4">
        <QuestionHeading number={item.number} points={item.points} />
        <span className="text-xs text-slate-600">{vi.questions.difficulties[item.difficulty]}</span>
      </div>
      <MathMarkdown>{item.stem}</MathMarkdown>
      {item.type === "MultipleChoice" && "correctLabel" in item.payload && (
        <p className="font-semibold">
          {t.answerLabel}: {item.payload.correctLabel}
        </p>
      )}
      <QuestionPayloadView type={item.type} payload={item.payload} />
      {item.explanation && (
        <div className="text-sm">
          <p className="font-medium">{t.explanation}</p>
          <MathMarkdown>{item.explanation}</MathMarkdown>
        </div>
      )}
    </article>
  );
}
