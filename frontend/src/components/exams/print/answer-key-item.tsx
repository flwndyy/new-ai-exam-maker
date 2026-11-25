"use client";

import { MathMarkdown } from "@/components/math-markdown";
import { QuestionHeading } from "@/components/exams/print/student-sheet-item";
import type { components } from "@/lib/api/schema";
import { formatDecimalForInput } from "@/lib/format/decimal";
import { vi } from "@/messages/vi";

type AnswerKeyItem = components["schemas"]["AnswerKeyItem"];

/** Black-on-white rendering of the answer payload: the printed key never relies on colour. */
function AnswerPayload({ item }: { item: AnswerKeyItem }) {
  const t = vi.exams.print;
  const payload = item.payload;
  if (item.type === "MultipleChoice" && "options" in payload) {
    return (
      <ol className="mt-1 grid grid-cols-1 gap-x-5 gap-y-0.5 pl-[18px] sm:grid-cols-2 print:grid-cols-2">
        {payload.options.map((o) => (
          <li key={o.label} className={`flex gap-1.5 ${o.label === payload.correctLabel ? "font-bold underline" : ""}`}>
            <span className="font-semibold">{o.label}.</span>
            <div className="min-w-0 flex-1">
              <MathMarkdown>{o.text}</MathMarkdown>
            </div>
          </li>
        ))}
      </ol>
    );
  }
  if (item.type === "TrueFalse" && "statements" in payload) {
    return (
      <ul className="mt-1 space-y-0.5 pl-[18px]">
        {payload.statements.map((s) => (
          <li key={s.label} className="flex items-baseline justify-between gap-4">
            <div className="flex min-w-0 flex-1 gap-1.5">
              <span className="font-semibold">{s.label})</span>
              <div className="min-w-0 flex-1">
                <MathMarkdown>{s.text}</MathMarkdown>
              </div>
            </div>
            <span className="shrink-0 font-bold">{s.isTrue ? vi.questions.trueLabel : vi.questions.falseLabel}</span>
          </li>
        ))}
      </ul>
    );
  }
  if (item.type === "ShortAnswer" && "acceptedAnswers" in payload) {
    return (
      <div className="mt-1 pl-[18px]">
        <p>
          <span className="font-semibold">{vi.questions.shortAnswerValue}:</span> {payload.acceptedAnswers.join(" | ")}
          {payload.unit ? ` (${payload.unit})` : ""}
        </p>
        {payload.tolerance != null && (
          <p>
            <span className="font-semibold">{vi.questions.shortTolerance}:</span> ±{formatDecimalForInput(payload.tolerance)}
          </p>
        )}
      </div>
    );
  }
  if (item.type === "Essay" && "rubric" in payload) {
    return (
      <div className="mt-1 pl-[18px]">
        <p className="font-semibold">{vi.questions.modelAnswer}:</p>
        <MathMarkdown>{payload.modelAnswer}</MathMarkdown>
        <p className="mt-1 font-semibold">{vi.questions.essayRubric}:</p>
        <ul>
          {payload.rubric.map((r, idx) => (
            <li key={idx} className="flex justify-between gap-4">
              <span>{r.criterion}</span>
              <span className="shrink-0">
                {formatDecimalForInput(r.points)} {t.pointsUnit}
              </span>
            </li>
          ))}
        </ul>
      </div>
    );
  }
  return null;
}

/** Teacher answer block: stem, full payload with answers, difficulty and explanation. */
export function AnswerKeyItemView({ item }: { item: AnswerKeyItem }) {
  const t = vi.exams.print;
  return (
    <article className="print-item mb-3 text-black">
      <div className="flex gap-1.5">
        <QuestionHeading number={item.number} points={item.points} />
        <div className="min-w-0 flex-1">
          <MathMarkdown>{item.stem}</MathMarkdown>
        </div>
        <span className="shrink-0 text-[12.5px]">{vi.questions.difficulties[item.difficulty]}</span>
      </div>
      {item.type === "MultipleChoice" && "correctLabel" in item.payload && (
        <p className="mt-1 pl-[18px] font-bold">
          {t.answerLabel}: {item.payload.correctLabel}
        </p>
      )}
      <AnswerPayload item={item} />
      {item.explanation && (
        <div className="mt-1 pl-[18px] text-[13px]">
          <p className="font-semibold italic">{t.explanation}</p>
          <MathMarkdown>{item.explanation}</MathMarkdown>
        </div>
      )}
    </article>
  );
}
