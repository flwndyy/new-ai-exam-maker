import { describe, expect, it } from "vitest";
import { sanitizeReturnTo } from "./return-to";

const cases: Array<[string | null | undefined, string]> = [
  ["/exams/view/?id=1#q", "/exams/view/?id=1#q"],
  [null, "/"],
  [undefined, "/"],
  ["", "/"],
  ["exams", "/"],
  ["//evil.example/x", "/"],
  ["/\\evil.example", "/"],
  ["https://evil.example/", "/"],
  ["javascript:alert(1)", "/"],
  ["/auth/callback/?code=x", "/"],
  ["/auth/callback", "/"],
  // Adversarial: normalization must not produce a scheme-relative result.
  ["/.//evil.example", "/"],
  ["/a/..//evil.example", "/"],
  ["/\t/evil.example", "/"],
  ["/\n\\evil.example", "/"],
];

describe("sanitizeReturnTo", () => {
  it.each(cases)("sanitizeReturnTo(%j) -> %j", (input, expected) => {
    expect(sanitizeReturnTo(input)).toBe(expected);
  });

  it("keeps percent-encoded slashes as a same-origin path", () => {
    const result = sanitizeReturnTo("/%2F%2Fevil");
    expect(result.startsWith("/")).toBe(true);
    expect(result.startsWith("//")).toBe(false);
  });

  it("never returns a scheme-relative or backslash-prefixed result", () => {
    for (const [input] of cases) {
      const result = sanitizeReturnTo(input);
      expect(result.startsWith("//")).toBe(false);
      expect(result.startsWith("/\\")).toBe(false);
    }
  });
});
