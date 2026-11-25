import type { components } from "@/lib/api/schema";

type Question = components["schemas"]["Question"];
type AiJob = components["schemas"]["AiJob"];
type Exam = components["schemas"]["Exam"];

export type UnreviewedAiGroup = {
  aiJobId: string;
  count: number;
  subjectId: string;
  topicId: string;
  grade: number;
  firstCreatedAt: string;
};

const FAILED_JOB_WINDOW_MS = 24 * 60 * 60 * 1000;

/** Groups the author's AI drafts that still need a review by job, newest group first. */
export function groupUnreviewedAiQuestions(items: Question[]): UnreviewedAiGroup[] {
  const groups = new Map<string, UnreviewedAiGroup>();
  for (const item of items) {
    if (item.source !== "AI" || item.aiReviewedByAuthor || !item.aiJobId) continue;
    const existing = groups.get(item.aiJobId);
    if (existing) {
      existing.count += 1;
      if (Date.parse(item.createdAt) < Date.parse(existing.firstCreatedAt)) {
        existing.firstCreatedAt = item.createdAt;
      }
    } else {
      groups.set(item.aiJobId, {
        aiJobId: item.aiJobId,
        count: 1,
        subjectId: item.subjectId,
        topicId: item.topicId,
        grade: item.grade,
        firstCreatedAt: item.createdAt,
      });
    }
  }
  return [...groups.values()].sort((a, b) => Date.parse(b.firstCreatedAt) - Date.parse(a.firstCreatedAt));
}

/** Jobs worth a look: still waiting or running, or failed within the 24 hours before `now`. */
export function attentionJobs(jobs: AiJob[], now: Date): AiJob[] {
  return jobs.filter((job) => {
    if (job.status === "Queued" || job.status === "Running") return true;
    if (job.status !== "Failed" || !job.finishedAt) return false;
    const finished = Date.parse(job.finishedAt);
    return !Number.isNaN(finished) && now.getTime() - finished <= FAILED_JOB_WINDOW_MS;
  });
}

/** Exams whose latest version was rejected by the reviewer. */
export function rejectedExams(exams: Exam[]): Exam[] {
  return exams.filter((exam) => exam.latestVersionStatus === "Rejected");
}
