import { describe, expect, it } from "vitest";

import { describeJob, STALE_QUEUED_MS } from "@/lib/ai/job-status";
import type { components } from "@/lib/api/schema";
import { vi as messages } from "@/messages/vi";

type AiJob = components["schemas"]["AiJob"];

const createdAt = "2026-11-23T08:00:00Z";
const now = new Date("2026-11-23T08:05:00Z");

function job(overrides: Partial<AiJob>): AiJob {
  return {
    id: "00000000-0000-0000-0000-0000000000a1",
    requestedBy: { id: "00000000-0000-0000-0000-000000000001", fullName: "Synthetic User" },
    status: "Queued",
    params: {
      subjectId: "00000000-0000-0000-0000-000000000020",
      topicId: "00000000-0000-0000-0000-000000000030",
      grade: 10,
      type: "MultipleChoice",
      difficulty: "Recognition",
      count: 5,
      note: null,
    },
    attempt: 0,
    maxAttempts: 3,
    errorCode: null,
    errorMessage: null,
    modelId: null,
    promptVersion: null,
    inputTokens: 0,
    outputTokens: 0,
    duplicatesDropped: 0,
    questionIds: [],
    createdAt,
    startedAt: null,
    finishedAt: null,
    ...overrides,
  };
}

describe("describeJob", () => {
  it("maps a fresh queued job to waiting", () => {
    expect(describeJob(job({}), now)).toEqual({ kind: "waiting", attempt: 0, maxAttempts: 3, stale: false, message: null });
  });

  it("maps a queued job with attempts to retrying with the server message", () => {
    const d = describeJob(job({ attempt: 1, errorMessage: "Dịch vụ AI đang quá tải, hệ thống sẽ thử lại." }), now);
    expect(d.kind).toBe("retrying");
    expect(d.attempt).toBe(1);
    expect(d.message).toBe("Dịch vụ AI đang quá tải, hệ thống sẽ thử lại.");
  });

  it("maps running and succeeded", () => {
    expect(describeJob(job({ status: "Running", attempt: 1 }), now).kind).toBe("running");
    expect(describeJob(job({ status: "Succeeded", attempt: 1 }), now).kind).toBe("succeeded");
  });

  it("maps failed with the server message, falling back to a generic one", () => {
    expect(describeJob(job({ status: "Failed", errorMessage: "Lỗi X" }), now).message).toBe("Lỗi X");
    expect(describeJob(job({ status: "Failed" }), now).message).toBe(messages.ai.failedFallback);
  });

  it("flags a queued job as stale only after more than 15 minutes", () => {
    const created = new Date(createdAt).getTime();
    expect(describeJob(job({}), new Date(created + STALE_QUEUED_MS)).stale).toBe(false);
    expect(describeJob(job({}), new Date(created + STALE_QUEUED_MS + 1)).stale).toBe(true);
    expect(describeJob(job({ status: "Running" }), new Date(created + STALE_QUEUED_MS + 1)).stale).toBe(false);
  });
});
