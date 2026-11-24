// UI-side exam composition rules. The API remains the authority (docs/context.md §3.2, §4.3);
// these only decide what to show.

import type { ItemView } from "@/lib/exams/composer-schema";
import type { components } from "@/lib/api/schema";

type Question = components["schemas"]["Question"];
type ExamVersionSummary = components["schemas"]["ExamVersionSummary"];
type ExamVersionStatus = components["schemas"]["ExamVersionStatus"];

export type Role = "teacher" | "head" | "admin";

export type VersionActionFlags = {
  canEdit: boolean;
  submit: boolean;
  withdraw: boolean;
  archive: boolean;
  createVersion: boolean;
};

const EDITABLE: ReadonlySet<ExamVersionStatus> = new Set(["Draft", "Rejected"]);

const EXPORTABLE: ReadonlySet<ExamVersionStatus> = new Set(["Submitted", "Approved", "Published"]);

/** Only frozen, never-archived versions can be exported (docs/context.md §4.4). */
export function canExport(status: ExamVersionStatus): boolean {
  return EXPORTABLE.has(status);
}

/** A bank question can be added when it is the author's own (not archived) or approved. */
export function isSelectable(q: Question, meId: string): boolean {
  return (q.createdBy.id === meId && q.status !== "Archived") || q.status === "Approved";
}

/** True when a saved item's source changed revision since it was picked. */
export function hasRevisionDrift(v: ItemView): boolean {
  return v.sourceRevision !== null && v.currentSourceRevision !== null && v.sourceRevision !== v.currentSourceRevision;
}

/** Requested version if it belongs to the exam; else newest Draft/Rejected; else newest. Versions are versionNo desc. */
export function pickDefaultVersionId(versions: ExamVersionSummary[], requested: string | null): string | undefined {
  if (requested && versions.some((v) => v.id === requested)) return requested;
  return versions.find((v) => EDITABLE.has(v.status))?.id ?? versions[0]?.id;
}

/** Author-only controls per version status (spec 5.3 table). */
export function versionActions(a: {
  status: ExamVersionStatus;
  isAuthor: boolean;
  role: Role;
  hasDraft: boolean;
}): VersionActionFlags {
  const flags: VersionActionFlags = { canEdit: false, submit: false, withdraw: false, archive: false, createVersion: false };
  if (!a.isAuthor || a.role === "admin") return flags;
  switch (a.status) {
    case "Draft":
      return { ...flags, canEdit: true, submit: true };
    case "Rejected":
      return { ...flags, canEdit: true, submit: true, archive: true, createVersion: !a.hasDraft };
    case "Submitted":
      return { ...flags, withdraw: true };
    case "Approved":
      return { ...flags, archive: true, createVersion: !a.hasDraft };
    case "Published":
    case "Archived":
      return { ...flags, createVersion: !a.hasDraft };
  }
}
