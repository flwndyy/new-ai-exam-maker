import { describe, expect, it } from "vitest";

import { splitPointsEvenly, sumDecimalStrings } from "@/lib/exams/points";

describe("sumDecimalStrings", () => {
  it("adds comma and dot decimals without float drift", () => {
    expect(sumDecimalStrings(["0.1", "0,2"])).toBe("0.3");
  });

  it("treats blank and invalid entries as zero and strips trailing zeros", () => {
    expect(sumDecimalStrings(["1.25", "1.25", "", "abc"])).toBe("2.5");
  });

  it("returns 0 for an empty list", () => {
    expect(sumDecimalStrings([])).toBe("0");
  });

  it("handles mixed precision", () => {
    expect(sumDecimalStrings(["3.33", "3.33", "3.34"])).toBe("10");
    expect(sumDecimalStrings(["0.125", "2"])).toBe("2.125");
  });
});

describe("splitPointsEvenly", () => {
  it("puts the cent remainder on the last item", () => {
    expect(splitPointsEvenly(10, 3)).toEqual(["3.33", "3.33", "3.34"]);
  });

  it("splits exactly when divisible", () => {
    expect(splitPointsEvenly(10, 8)).toEqual(Array(8).fill("1.25"));
    expect(splitPointsEvenly(10, 4)).toEqual(["2.5", "2.5", "2.5", "2.5"]);
  });

  it("returns an empty list for no items", () => {
    expect(splitPointsEvenly(10, 0)).toEqual([]);
  });

  it("always sums back to the total", () => {
    for (const n of [1, 6, 7, 9, 11, 30]) {
      expect(sumDecimalStrings(splitPointsEvenly(10, n))).toBe("10");
    }
  });
});
