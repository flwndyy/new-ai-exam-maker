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
  const unapproved = view.sourceStatus !== null && view.sourceStatus !== "Approved";
  const meta = [view.type ? vi.questions.types[view.type] : null, view.difficulty ? vi.questions.difficulties[view.difficulty] : null]
    .filter(Boolean)
    .join(", ");

  return (
    <li
      data-highlighted={highlighted ? "true" : undefined}
      data-invalid={highlighted ? "true" : undefined}
      className={`mb-2 grid grid-cols-[26px_minmax(0,1fr)] gap-x-3 gap-y-2 rounded-md bg-white px-3 py-3 ring-1 sm:grid-cols-[26px_minmax(0,1fr)_auto] ${
        highlighted ? "ring-rej" : "ring-line"
      }`}
    >
      <span className="pt-px text-[13.5px] font-bold text-muted tabular-nums">
        <span className="sr-only">{vi.review.questionItemPrefix} </span>
        {index + 1}
      </span>

      <div className="min-w-0 break-words [&_.katex-display]:max-w-full [&_.katex-display]:overflow-x-auto">
        {hasContent ? (
          <>
            <div className={expanded ? "" : "line-clamp-3"}>
              <MathMarkdown>{view.stem ?? ""}</MathMarkdown>
            </div>
            {meta && <p className="mt-0.5 text-[12.5px] text-muted">{meta}</p>}
            {expanded && view.type && view.payload && (
              <div className="mt-2 space-y-2">
                <QuestionPayloadView type={view.type} payload={view.payload} />
                {view.explanation && (
                  <div className="text-[13px] text-ink-soft">
                    <MathMarkdown>{view.explanation}</MathMarkdown>
                  </div>
                )}
              </div>
            )}
            <button type="button" onClick={() => setExpanded((e) => !e)} className="mt-1 text-xs text-muted underline">
              {expanded ? vi.exams.showLess : vi.exams.showMore}
            </button>
          </>
        ) : (
          <p className="text-[13.5px] text-muted">{vi.exams.contentUnavailable}</p>
        )}
        {hasRevisionDrift(view) && (
          <p className="mt-1 text-[12.5px] text-wait-text">
            <span aria-hidden="true">⚠ </span>
            <span>{vi.exams.revisionDrift}</span>
          </p>
        )}
        {(unapproved || highlighted) && (
          <p className={`mt-1 text-[12.5px] ${highlighted ? "text-rej" : "text-wait-text"}`}>{vi.exams.needsApproval}</p>
        )}
      </div>

      {children && <div className="col-start-2 flex flex-wrap items-center gap-1.5 sm:col-start-3 sm:row-start-1">{children}</div>}
    </li>
  );
}
