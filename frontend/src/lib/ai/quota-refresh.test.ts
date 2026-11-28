import { describe, expect, it } from "vitest";

import { QUOTA_REFRESH_MAX_MS, quotaRefreshDelay } from "@/lib/ai/quota-refresh";

const now = Date.parse("2026-11-23T23:59:50Z");

describe("quotaRefreshDelay", () => {
  it("reloads one second after resetsAt", () => {
    expect(quotaRefreshDelay("2026-11-24T00:00:00Z", now)).toBe(11_000);
  });

  it("re-checks at most hourly when the reset is far away", () => {
    expect(quotaRefreshDelay("2026-11-25T00:00:00Z", now)).toBe(QUOTA_REFRESH_MAX_MS);
    expect(QUOTA_REFRESH_MAX_MS).toBe(3_600_000);
  });

  it("retries soon when resetsAt is already in the past (clock skew)", () => {
    expect(quotaRefreshDelay("2026-11-23T00:00:00Z", now)).toBe(1000);
  });

  it("does not schedule anything without quota data or with an invalid date", () => {
    expect(quotaRefreshDelay(undefined, now)).toBe(false);
    expect(quotaRefreshDelay("not-a-date", now)).toBe(false);
  });
});
