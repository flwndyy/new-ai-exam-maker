import { describe, expect, it } from "vitest";

import type { components } from "@/lib/api/schema";

import { attentionJobs, groupUnreviewedAiQuestions, rejectedExams } from "./todo";

type Question = components["schemas"]["Question"];
type AiJob = components["schemas"]["AiJob"];
type Exam = components["schemas"]["Exam"];

const SUBJECT = "00000000-0000-0000-0000-000000000020";
const TOPIC = "00000000-0000-0000-0000-000000000030";

function question(over: Partial<Question>): Question {
  return {
    id: "q",
    subjectId: SUBJECT,
    topicId: TOPIC,
    departmentId: "d",
    grade: 10,
    type: "MultipleChoice",
    difficulty: "Recognition",
    stem: "Câu hỏi",
    payload: {} as Question["payload"],
    status: "Draft",
    revision: 1,
    source: "AI",
    aiJobId: "job-a",
    aiOrdinal: 1,
    aiVerification: "NotChecked",
    aiReviewedByAuthor: false,
    createdBy: { id: "u", fullName: "U" },
    createdAt: "2026-11-25T01:00:00Z",
    updatedAt: "2026-11-25T01:00:00Z",
    ...over,
  } as Question;
}

describe("groupUnreviewedAiQuestions", () => {
  it("groups unreviewed AI questions by job, newest group first", () => {
    const groups = groupUnreviewedAiQuestions([
      question({ id: "1", aiJobId: "job-a", createdAt: "2026-11-25T01:00:00Z" }),
      question({ id: "2", aiJobId: "job-a", createdAt: "2026-11-25T01:00:05Z" }),
      question({ id: "3", aiJobId: "job-b", createdAt: "2026-11-25T03:00:00Z", grade: 11 }),
      question({ id: "4", source: "Manual", aiJobId: null, aiOrdinal: null }),
      question({ id: "5", aiJobId: "job-c", aiReviewedByAuthor: true }),
    ]);
    expect(groups.map((g) => [g.aiJobId, g.count])).toEqual([
      ["job-b", 1],
      ["job-a", 2],
    ]);
    expect(groups[1]).toMatchObject({ subjectId: SUBJECT, topicId: TOPIC, grade: 10, firstCreatedAt: "2026-11-25T01:00:00Z" });
    expect(groups[0].grade).toBe(11);
  });

  it("returns nothing when there is no AI question to review", () => {
    expect(groupUnreviewedAiQuestions([])).toEqual([]);
  });
});

describe("attentionJobs", () => {
  const now = new Date("2026-11-25T12:00:00Z");
  const job = (over: Partial<AiJob>) => ({ id: "j", status: "Queued", ...over }) as AiJob;

  it("keeps Queued and Running jobs", () => {
    const jobs = [job({ id: "1", status: "Queued" }), job({ id: "2", status: "Running" })];
    expect(attentionJobs(jobs, now).map((j) => j.id)).toEqual(["1", "2"]);
  });

  it("keeps a Failed job from 23 hours ago and drops one from 25 hours ago", () => {
    const jobs = [
      job({ id: "recent", status: "Failed", finishedAt: "2026-11-24T13:00:00Z" }),
      job({ id: "old", status: "Failed", finishedAt: "2026-11-24T11:00:00Z" }),
      job({ id: "nodate", status: "Failed", finishedAt: null }),
    ];
    expect(attentionJobs(jobs, now).map((j) => j.id)).toEqual(["recent"]);
  });

  it("drops Succeeded jobs", () => {
    expect(attentionJobs([job({ status: "Succeeded", finishedAt: "2026-11-25T11:00:00Z" })], now)).toEqual([]);
  });
});

describe("rejectedExams", () => {
  it("keeps only exams whose latest version is Rejected", () => {
    const exams = [
      { id: "1", latestVersionStatus: "Rejected" },
      { id: "2", latestVersionStatus: "Draft" },
      { id: "3", latestVersionStatus: "Published" },
    ] as Exam[];
    expect(rejectedExams(exams).map((e) => e.id)).toEqual(["1"]);
  });
});
