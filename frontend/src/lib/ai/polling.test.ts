import { describe, expect, it } from "vitest";

import { isJobFinished, nextPollDelay, POLL_DELAYS_MS } from "@/lib/ai/polling";

describe("nextPollDelay", () => {
  it("backs off 2s, 4s, 8s then stays at 15s while queued", () => {
    expect([0, 1, 2, 3, 4, 5].map((n) => nextPollDelay(n, "Queued"))).toEqual([2000, 4000, 8000, 15000, 15000, 15000]);
  });

  it("uses the same schedule while running", () => {
    expect([0, 1, 2, 3, 10].map((n) => nextPollDelay(n, "Running"))).toEqual([2000, 4000, 8000, 15000, 15000]);
  });

  it("stops polling once the job has finished", () => {
    expect(nextPollDelay(0, "Succeeded")).toBe(false);
    expect(nextPollDelay(7, "Failed")).toBe(false);
  });

  it("polls quickly before the first response arrives", () => {
    expect(nextPollDelay(0, undefined)).toBe(2000);
  });

  it("treats a negative count as the first poll", () => {
    expect(nextPollDelay(-1, "Queued")).toBe(2000);
  });

  it("exposes the schedule from the spec", () => {
    expect(POLL_DELAYS_MS).toEqual([2000, 4000, 8000, 15000]);
  });
});

describe("isJobFinished", () => {
  it("is true only for Succeeded and Failed", () => {
    expect(isJobFinished("Succeeded")).toBe(true);
    expect(isJobFinished("Failed")).toBe(true);
    expect(isJobFinished("Queued")).toBe(false);
    expect(isJobFinished("Running")).toBe(false);
  });
});
