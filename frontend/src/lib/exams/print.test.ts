import { afterEach, describe, expect, it, vi } from "vitest";

import { parsePrintParams, printUrl, schoolName } from "@/lib/exams/print";

describe("parsePrintParams", () => {
  it("accepts a version id with a known mode and keeps the optional exam id", () => {
    expect(parsePrintParams(new URLSearchParams("vid=V&mode=student&id=E"))).toEqual({ ok: true, vid: "V", mode: "student", examId: "E" });
    expect(parsePrintParams(new URLSearchParams("vid=V&mode=answer"))).toEqual({ ok: true, vid: "V", mode: "answer", examId: null });
  });

  it("rejects a missing version id or an unknown mode", () => {
    expect(parsePrintParams(new URLSearchParams("mode=student")).ok).toBe(false);
    expect(parsePrintParams(new URLSearchParams("vid=V&mode=foo")).ok).toBe(false);
    expect(parsePrintParams(new URLSearchParams("vid=V")).ok).toBe(false);
  });
});

describe("printUrl", () => {
  it("builds the static-export print route", () => {
    expect(printUrl("E", "V", "answer")).toBe("/exams/print/?id=E&vid=V&mode=answer");
  });
});

describe("schoolName", () => {
  afterEach(() => vi.unstubAllEnvs());

  it("returns the trimmed configured name or null when blank", () => {
    vi.stubEnv("NEXT_PUBLIC_SCHOOL_NAME", "  THPT Mẫu  ");
    expect(schoolName()).toBe("THPT Mẫu");
    vi.stubEnv("NEXT_PUBLIC_SCHOOL_NAME", "   ");
    expect(schoolName()).toBeNull();
  });
});
