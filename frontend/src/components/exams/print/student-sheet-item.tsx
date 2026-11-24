"use client";

import { MathMarkdown } from "@/components/math-markdown";
import type { components } from "@/lib/api/schema";
import { formatDecimalForInput } from "@/lib/format/decimal";
import { vi } from "@/messages/vi";

type StudentSheetItem = components["schemas"]["StudentSheetItem"];

export function QuestionHeading({ number, points }: { number: number; points: number }) {
  const t = vi.exams.print;
  return (
    <span className="font-semibold">
      {t.question} {number} ({formatDecimalForInput(points)} {t.pointsUnit}).
    </span>
  );
}

/**
 * Student question block. It reads ONLY number/points/stem and, per type, option/statement label+text and
 * answerType/unit. Any other payload key (even if a server bug sent one) is never touched, so answers cannot render.
 */
export function StudentSheetItemView({ item }: { item: StudentSheetItem }) {
  const t = vi.exams.print;
  const payload = item.payload as Record<string, unknown>;
  return (
    <article className="print-item space-y-2">
      <QuestionHeading number={item.number} points={item.points} />
      <MathMarkdown>{item.stem}</MathMarkdown>
      {item.type === "MultipleChoice" && Array.isArray(payload.options) && (
        <ol className="grid grid-cols-1 gap-x-6 gap-y-1 pl-2 sm:grid-cols-2 print:grid-cols-2">
          {(payload.options as { label: string; text: string }[]).map((o) => (
            <li key={o.label} className="flex gap-2">
              <span className="font-semibold">{o.label}.</span>
              <MathMarkdown>{o.text}</MathMarkdown>
            </li>
          ))}
        </ol>
      )}
      {item.type === "TrueFalse" && Array.isArray(payload.statements) && (
        <table className="w-full border-collapse text-sm">
          <thead>
            <tr>
              <th className="border border-slate-400 px-2 py-1 text-left" />
              <th className="w-28 border border-slate-400 px-2 py-1">{t.trueFalseHeader}</th>
            </tr>
          </thead>
          <tbody>
            {(payload.statements as { label: string; text: string }[]).map((s) => (
              <tr key={s.label}>
                <td className="border border-slate-400 px-2 py-1">
                  <span className="mr-2 font-semibold">{s.label})</span>
                  <MathMarkdown>{s.text}</MathMarkdown>
                </td>
                <td className="border border-slate-400 px-2 py-1" />
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {item.type === "ShortAnswer" && (
        <p className="text-sm">
          {t.answerLine}: ........................................ {typeof payload.unit === "string" ? payload.unit : ""}
        </p>
      )}
      {item.type === "Essay" && <div className="h-40 border-b border-dashed border-slate-300" aria-hidden />}
    </article>
  );
}
