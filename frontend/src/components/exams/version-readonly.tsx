"use client";

import type { ReactNode } from "react";

import { VersionItemCard } from "@/components/exams/version-item-card";
import { EmptyState } from "@/components/ui/empty-state";
import { MetaBar } from "@/components/ui/meta-bar";
import type { components } from "@/lib/api/schema";
import { itemViewFromVersionItem } from "@/lib/exams/composer-schema";
import { formatDecimalForInput } from "@/lib/format/decimal";
import { vi } from "@/messages/vi";

type ExamVersion = components["schemas"]["ExamVersion"];

/** Frozen or non-author view of a version: snapshot items, totals and review metadata. `actions` sits in the meta bar. */
export function VersionReadonly({ version, actions }: { version: ExamVersion; actions?: ReactNode }) {
  const items: { label: ReactNode; value: ReactNode }[] = [
    { label: vi.exams.duration, value: version.durationMinutes },
    { label: vi.exams.questionCount, value: version.questionCount },
    { label: vi.exams.totalPoints, value: formatDecimalForInput(version.totalPoints) },
  ];
  if (version.contentHash) {
    items.push({
      label: vi.exams.contentHash,
      value: (
        <span className="font-mono text-[14px]" title={version.contentHash}>
          {version.contentHash.slice(0, 12)}
        </span>
      ),
    });
  }
  if (version.reviewedBy) items.push({ label: vi.exams.reviewedBy, value: <span className="text-[14px]">{version.reviewedBy.fullName}</span> });
  if (version.publishedBy) items.push({ label: vi.exams.publishedBy, value: <span className="text-[14px]">{version.publishedBy.fullName}</span> });

  return (
    <div>
      <MetaBar items={items} actions={actions} />
      {version.items.length === 0 ? (
        <EmptyState title={vi.exams.noItems} className="min-h-[120px]" />
      ) : (
        <ol>
          {version.items.map((item, index) => (
            <VersionItemCard key={item.sourceQuestionId} index={index} view={itemViewFromVersionItem(item)}>
              <span className="text-[13px] tabular-nums text-ink-soft">{`${vi.exams.pointsLabel}: ${formatDecimalForInput(item.points)}`}</span>
            </VersionItemCard>
          ))}
        </ol>
      )}
    </div>
  );
}
