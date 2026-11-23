"use client";

import { VersionItemCard } from "@/components/exams/version-item-card";
import type { components } from "@/lib/api/schema";
import { itemViewFromVersionItem } from "@/lib/exams/composer-schema";
import { formatDecimalForInput } from "@/lib/format/decimal";
import { vi } from "@/messages/vi";

type ExamVersion = components["schemas"]["ExamVersion"];

/** Frozen or non-author view of a version: snapshot items, totals and review metadata. */
export function VersionReadonly({ version }: { version: ExamVersion }) {
  return (
    <div className="space-y-4">
      <dl className="flex flex-wrap gap-x-6 gap-y-1 text-sm text-slate-700">
        <div>
          <dt className="inline font-medium">{vi.exams.duration}: </dt>
          <dd className="inline">{version.durationMinutes}</dd>
        </div>
        <div>
          <dt className="inline font-medium">{vi.exams.totalPoints}: </dt>
          <dd className="inline">{formatDecimalForInput(version.totalPoints)}</dd>
        </div>
        <div>
          <dt className="inline font-medium">{vi.exams.questionCount}: </dt>
          <dd className="inline">{version.questionCount}</dd>
        </div>
        {version.contentHash && (
          <div>
            <dt className="inline font-medium">{vi.exams.contentHash}: </dt>
            <dd className="inline font-mono" title={version.contentHash}>
              {version.contentHash.slice(0, 12)}
            </dd>
          </div>
        )}
        {version.reviewedBy && (
          <div>
            <dt className="inline font-medium">{vi.exams.reviewedBy}: </dt>
            <dd className="inline">{version.reviewedBy.fullName}</dd>
          </div>
        )}
        {version.publishedBy && (
          <div>
            <dt className="inline font-medium">{vi.exams.publishedBy}: </dt>
            <dd className="inline">{version.publishedBy.fullName}</dd>
          </div>
        )}
      </dl>
      {version.items.length === 0 ? (
        <p className="text-sm text-slate-500">{vi.exams.noItems}</p>
      ) : (
        <ol className="space-y-2">
          {version.items.map((item, index) => (
            <VersionItemCard key={item.sourceQuestionId} index={index} view={itemViewFromVersionItem(item)}>
              <span className="text-sm text-slate-700">{`${vi.exams.pointsLabel}: ${formatDecimalForInput(item.points)}`}</span>
            </VersionItemCard>
          ))}
        </ol>
      )}
    </div>
  );
}
