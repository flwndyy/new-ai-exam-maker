import { describe, expect, it } from "vitest";

import type { ItemView } from "@/lib/exams/composer-schema";
import { canExport, hasRevisionDrift, isSelectable, pickDefaultVersionId, versionActions } from "@/lib/exams/rules";
import type { components } from "@/lib/api/schema";

type Question = components["schemas"]["Question"];
type ExamVersionSummary = components["schemas"]["ExamVersionSummary"];
type ExamVersionStatus = components["schemas"]["ExamVersionStatus"];

const ME = "me";

function q(status: Question["status"], createdBy: string): Question {
  return { id: "q", status, createdBy: { id: createdBy, fullName: "X" } } as unknown as Question;
}

function v(id: string, status: ExamVersionStatus): ExamVersionSummary {
  return { id, status } as unknown as ExamVersionSummary;
}

describe("isSelectable", () => {
  it("allows own non-archived questions", () => {
    expect(isSelectable(q("Draft", ME), ME)).toBe(true);
    expect(isSelectable(q("Rejected", ME), ME)).toBe(true);
    expect(isSelectable(q("Archived", ME), ME)).toBe(false);
  });

  it("allows other people's questions only when approved", () => {
    expect(isSelectable(q("Approved", "other"), ME)).toBe(true);
    expect(isSelectable(q("Submitted", "other"), ME)).toBe(false);
  });
});

describe("hasRevisionDrift", () => {
  const base: ItemView = {
    stem: "s",
    type: null,
    difficulty: null,
    payload: null,
    explanation: null,
    sourceRevision: 2,
    currentSourceRevision: 3,
    sourceStatus: null,
  };

  it("flags differing revisions", () => expect(hasRevisionDrift(base)).toBe(true));
  it("ignores equal revisions", () => expect(hasRevisionDrift({ ...base, currentSourceRevision: 2 })).toBe(false));
  it("ignores unknown current revision", () => expect(hasRevisionDrift({ ...base, currentSourceRevision: null })).toBe(false));
  it("ignores unsaved bank items", () => expect(hasRevisionDrift({ ...base, sourceRevision: null })).toBe(false));
});

describe("pickDefaultVersionId", () => {
  const versions = [v("v3", "Approved"), v("v2", "Rejected"), v("v1", "Archived")];

  it("uses the requested version when it belongs to the exam", () => {
    expect(pickDefaultVersionId(versions, "v1")).toBe("v1");
  });

  it("falls back to the newest editable version for a foreign request", () => {
    expect(pickDefaultVersionId(versions, "elsewhere")).toBe("v2");
  });

  it("uses the newest version when none is editable", () => {
    expect(pickDefaultVersionId([v("v2", "Published"), v("v1", "Archived")], null)).toBe("v2");
  });

  it("returns undefined without versions", () => {
    expect(pickDefaultVersionId([], null)).toBeUndefined();
  });
});

describe("versionActions", () => {
  const none = { canEdit: false, submit: false, withdraw: false, archive: false, createVersion: false };

  it.each<[ExamVersionStatus, boolean, object]>([
    ["Draft", false, { ...none, canEdit: true, submit: true }],
    ["Draft", true, { ...none, canEdit: true, submit: true }],
    ["Rejected", false, { ...none, canEdit: true, submit: true, archive: true, createVersion: true }],
    ["Rejected", true, { ...none, canEdit: true, submit: true, archive: true }],
    ["Submitted", false, { ...none, withdraw: true }],
    ["Approved", false, { ...none, archive: true, createVersion: true }],
    ["Approved", true, { ...none, archive: true }],
    ["Published", false, { ...none, createVersion: true }],
    ["Published", true, none],
    ["Archived", false, { ...none, createVersion: true }],
    ["Archived", true, none],
  ])("author on %s (hasDraft=%s)", (status, hasDraft, expected) => {
    for (const role of ["teacher", "head"] as const) {
      expect(versionActions({ status, isAuthor: true, role, hasDraft })).toEqual(expected);
    }
  });

  it.each<ExamVersionStatus>(["Draft", "Rejected", "Submitted", "Approved", "Published", "Archived"])(
    "non-author head and admin get nothing on %s",
    (status) => {
      expect(versionActions({ status, isAuthor: false, role: "head", hasDraft: false })).toEqual(none);
      expect(versionActions({ status, isAuthor: false, role: "admin", hasDraft: false })).toEqual(none);
      expect(versionActions({ status, isAuthor: true, role: "admin", hasDraft: false })).toEqual(none);
    },
  );
});

describe("canExport", () => {
  it("allows only frozen, exportable statuses", () => {
    expect((["Submitted", "Approved", "Published"] as const).every(canExport)).toBe(true);
    expect((["Draft", "Rejected", "Archived"] as const).some(canExport)).toBe(false);
  });
});
