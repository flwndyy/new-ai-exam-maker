import { describe, expect, it } from "vitest";

import {
  type ComposerItem,
  composerSchema,
  itemViewFromQuestion,
  itemViewFromVersionItem,
  toFormValues,
} from "@/lib/exams/composer-schema";
import type { components } from "@/lib/api/schema";
import { vi as messages } from "@/messages/vi";

type ExamVersion = components["schemas"]["ExamVersion"];
type Question = components["schemas"]["Question"];

const emptyView = {
  stem: "x",
  type: null,
  difficulty: null,
  payload: null,
  explanation: null,
  sourceRevision: null,
  currentSourceRevision: null,
  sourceStatus: null,
} as const;

function item(id: string, points: string): ComposerItem {
  return { sourceQuestionId: id, points, view: { ...emptyView } };
}

function issues(input: unknown) {
  const result = composerSchema.safeParse(input);
  expect(result.success).toBe(false);
  return result.success ? [] : result.error.issues;
}

describe("composerSchema", () => {
  it("converts comma points and duration to the request body and drops view data", () => {
    const result = composerSchema.parse({ durationMinutes: "45", items: [item("q1", "1,5")] });
    expect(result).toEqual({ durationMinutes: 45, items: [{ sourceQuestionId: "q1", points: 1.5 }] });
  });

  it("accepts an empty draft", () => {
    expect(composerSchema.parse({ durationMinutes: "45", items: [] })).toEqual({ durationMinutes: 45, items: [] });
  });

  it.each(["0", "-1", "1e2", "", "0,0"])("rejects points %j at the item path", (points) => {
    const found = issues({ durationMinutes: "45", items: [item("q1", points)] });
    expect(found.some((i) => i.path.join(".") === "items.0.points" && i.message === messages.exams.validation.pointsInvalid)).toBe(true);
  });

  it.each(["0", "601", "4.5", ""])("rejects duration %j", (durationMinutes) => {
    const found = issues({ durationMinutes, items: [] });
    expect(found.some((i) => i.path.join(".") === "durationMinutes" && i.message === messages.exams.validation.durationInvalid)).toBe(true);
  });

  it("rejects duplicate sources at the items path", () => {
    const found = issues({ durationMinutes: "45", items: [item("q1", "1"), item("q1", "2")] });
    expect(found.some((i) => i.path.join(".") === "items" && i.message === messages.exams.validation.duplicate)).toBe(true);
  });

  it("rejects more than 200 items", () => {
    const items = Array.from({ length: 201 }, (_, i) => item(`q${i}`, "1"));
    const found = issues({ durationMinutes: "45", items });
    expect(found.some((i) => i.message === messages.exams.validation.tooMany)).toBe(true);
  });
});

describe("form value mapping", () => {
  const version = {
    durationMinutes: 60,
    items: [
      {
        sortOrder: 1,
        points: 1.25,
        sourceQuestionId: "q1",
        sourceRevision: 2,
        currentSourceRevision: 3,
        type: "MultipleChoice",
        difficulty: "Recognition",
        stem: "Câu 1",
        payload: null,
        explanation: null,
      },
    ],
  } as unknown as ExamVersion;

  it("maps a version into string form values with view data", () => {
    const values = toFormValues(version);
    expect(values.durationMinutes).toBe("60");
    expect(values.items[0].points).toBe("1.25");
    expect(values.items[0].view.currentSourceRevision).toBe(3);
    expect(values.items[0].view.sourceRevision).toBe(2);
  });

  it("version items have no known source status", () => {
    expect(itemViewFromVersionItem(version.items[0]).sourceStatus).toBeNull();
  });

  it("bank questions carry their status but no revision drift data", () => {
    const q = { id: "q9", stem: "S", type: "Essay", difficulty: "Application", payload: null, explanation: "E", status: "Draft", revision: 4 } as unknown as Question;
    const view = itemViewFromQuestion(q);
    expect(view).toMatchObject({ stem: "S", sourceStatus: "Draft", sourceRevision: null, currentSourceRevision: null, explanation: "E" });
  });
});
