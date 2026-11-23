import { describe, expect, it } from "vitest";

import { makeGenerateSchema } from "@/lib/ai/generate-schema";

const valid = {
  subjectId: "00000000-0000-0000-0000-000000000020",
  grade: "10",
  topicId: "00000000-0000-0000-0000-000000000030",
  type: "MultipleChoice",
  difficulty: "Recognition",
  count: "5",
  note: "",
};

describe("makeGenerateSchema", () => {
  const schema = makeGenerateSchema(10);

  it("parses a valid form into an AiGenerateRequest", () => {
    expect(schema.parse(valid)).toEqual({ ...valid, grade: 10, count: 5, note: null });
  });

  it("trims the note and turns a blank note into null", () => {
    expect(schema.parse({ ...valid, note: "   " }).note).toBeNull();
    expect(schema.parse({ ...valid, note: "  ôn tập  " }).note).toBe("ôn tập");
  });

  it("accepts a 1000-character note and rejects 1001", () => {
    expect(schema.safeParse({ ...valid, note: "x".repeat(1000) }).success).toBe(true);
    expect(schema.safeParse({ ...valid, note: "x".repeat(1001) }).success).toBe(false);
  });

  it.each(["0", "11", "2.5", "abc", ""])("rejects count %s for a quota max of 10", (count) => {
    expect(schema.safeParse({ ...valid, count }).success).toBe(false);
  });

  it("requires subject and topic", () => {
    expect(schema.safeParse({ ...valid, subjectId: "" }).success).toBe(false);
    expect(schema.safeParse({ ...valid, topicId: "" }).success).toBe(false);
  });
});
