"use client";

import { type ReactNode, useState } from "react";

import { MathMarkdown } from "@/components/math-markdown";
import { QuestionPayloadView } from "@/components/questions/question-payload-view";
import type { ItemView } from "@/lib/exams/composer-schema";
import { hasRevisionDrift } from "@/lib/exams/rules";
import { vi } from "@/messages/vi";

/** One question row of an exam version (composer or read-only). `children` holds row controls. */
export function VersionItemCard({
  index,
  view,
  highlighted = false,
  children,
}: {
  index: number;
  view: ItemView;
  highlighted?: boolean;
  children?: ReactNode;
}) {
  const [expanded, setExpanded] = useState(false);
  const hasContent = view.stem !== null;

  return (
    <li
      data-highlighted={highlighted ? "true" : undefined}
      className={`rounded border p-3 ${highlighted ? "border-rose-400 bg-rose-50" : "border-slate-200 bg-white"}`}
    >
      <div className="flex flex-wrap items-center gap-2 text-xs">
        <span className="font-semibold text-slate-900">{`${vi.review.questionItemPrefix} ${index + 1}`}</span>
        {view.type && <span className="text-slate-500">{vi.questions.types[view.type]}</span>}
        {view.difficulty && <span className="text-slate-500">{vi.questions.difficulties[view.difficulty]}</span>}
        {hasRevisionDrift(view) && (
          <span className="rounded bg-amber-100 px-2 py-0.5 text-amber-800">
            <span aria-hidden="true">⚠ </span>
            <span>{vi.exams.revisionDrift}</span>
          </span>
        )}
        {view.sourceStatus && view.sourceStatus !== "Approved" && (
          <span className="rounded bg-amber-50 px-2 py-0.5 text-amber-700">{vi.exams.needsApproval}</span>
        )}
      </div>

      {hasContent ? (
        <>
          <div className={`prose prose-sm mt-1 max-w-none ${expanded ? "" : "line-clamp-3"}`}>
            <MathMarkdown>{view.stem ?? ""}</MathMarkdown>
          </div>
          {expanded && view.type && view.payload && (
            <div className="mt-2 space-y-2">
              <QuestionPayloadView type={view.type} payload={view.payload} />
              {view.explanation && (
                <div className="prose prose-sm max-w-none text-slate-600">
                  <MathMarkdown>{view.explanation}</MathMarkdown>
                </div>
              )}
            </div>
          )}
          <button type="button" onClick={() => setExpanded((e) => !e)} className="mt-1 text-xs text-slate-600 underline">
            {expanded ? vi.exams.showLess : vi.exams.showMore}
          </button>
        </>
      ) : (
        <p className="mt-1 text-sm italic text-slate-500">{vi.exams.contentUnavailable}</p>
      )}

      {children && <div className="mt-2 flex flex-wrap items-center gap-2">{children}</div>}
    </li>
  );
}
