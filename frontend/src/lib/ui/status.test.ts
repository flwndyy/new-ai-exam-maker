import { describe, expect, it } from "vitest";

import { aiJobTone, aiVerificationTone, examVersionTone, questionTone, toneClass, userTone } from "./status";

describe("status tone mapping", () => {
  it.each([
    ["Draft", "draft"],
    ["Submitted", "wait"],
    ["Approved", "ok"],
    ["Rejected", "rej"],
    ["Archived", "arch"],
  ] as const)("question %s → %s", (s, t) => {
    expect(questionTone(s)).toBe(t);
  });

  it.each([
    ["Draft", "draft"],
    ["Submitted", "wait"],
    ["Approved", "ok"],
    ["Rejected", "rej"],
    ["Published", "pub"],
    ["Archived", "arch"],
  ] as const)("exam version %s → %s", (s, t) => {
    expect(examVersionTone(s)).toBe(t);
  });

  it.each([
    ["Queued", "draft"],
    ["Running", "wait"],
    ["Succeeded", "ok"],
    ["Failed", "rej"],
  ] as const)("ai job %s → %s", (s, t) => {
    expect(aiJobTone(s)).toBe(t);
  });

  it.each([
    ["Agree", "ok"],
    ["Disagree", "rej"],
    ["NotChecked", "neutral"],
  ] as const)("ai verification %s → %s", (v, t) => {
    expect(aiVerificationTone(v)).toBe(t);
  });

  it("user tone", () => {
    expect(userTone(true)).toBe("ok");
    expect(userTone(false)).toBe("arch");
  });

  it("wait text uses the darker shade", () => {
    expect(toneClass.wait.text).toBe("text-wait-text");
  });

  it("arch text uses the accessible grey", () => {
    expect(toneClass.arch.text).toBe("text-arch-text");
  });

  it("draft and arch use the tint background", () => {
    expect(toneClass.draft.bg).toBe("bg-tint");
    expect(toneClass.arch.bg).toBe("bg-tint");
  });

  it("text on tinted background uses the contrast-safe shades", () => {
    expect(toneClass.ok.onBg).toBe("text-ok-strong");
    expect(toneClass.arch.onBg).toBe("text-ink-soft");
    expect(toneClass.draft.onBg).toBe("text-ink-soft");
  });
});
