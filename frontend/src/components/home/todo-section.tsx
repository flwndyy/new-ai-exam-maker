"use client";

import { useQuery, useQueryClient, type QueryClient } from "@tanstack/react-query";
import Link from "next/link";
import { useId, type ReactNode } from "react";

import { RunningDot } from "@/components/ai/job-status-panel";
import { MathMarkdown } from "@/components/math-markdown";
import { Button } from "@/components/ui/button";
import { ListRow } from "@/components/ui/list-row";
import { Notice } from "@/components/ui/notice";
import { StatusText } from "@/components/ui/status-text";
import { api } from "@/lib/api/client";
import { getApiErrorMessage } from "@/lib/api/error";
import type { components } from "@/lib/api/schema";
import { attentionJobs, groupUnreviewedAiQuestions, rejectedExams } from "@/lib/home/todo";
import { aiJobTone, examVersionTone, questionTone } from "@/lib/ui/status";
import { vi } from "@/messages/vi";

type Role = components["schemas"]["Role"];
type Subject = components["schemas"]["Subject"];
type Topic = components["schemas"]["Topic"];

/** Rows shown per group; the rest is behind "Xem tất cả". */
const MAX_ROWS = 5;
/** The unreviewed-AI query reads a single page of this size, so a larger total means the count is a lower bound. */
const DRAFT_PAGE_SIZE = 100;

const dateFormat = new Intl.DateTimeFormat("vi-VN", { dateStyle: "short", timeZone: "Asia/Ho_Chi_Minh" });
const dateTimeFormat = new Intl.DateTimeFormat("vi-VN", {
  dateStyle: "short",
  timeStyle: "short",
  timeZone: "Asia/Ho_Chi_Minh",
});

function formatDate(value?: string | null): string | null {
  if (!value) return null;
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? null : dateFormat.format(date);
}

function formatDateTime(value?: string | null): string | null {
  if (!value) return null;
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? null : dateTimeFormat.format(date);
}

/** Names come only from catalog data other pages already cached; the home page never requests them itself. */
function cachedSubjectName(client: QueryClient, subjectId: string): string | undefined {
  const lists =
    client.getQueryData<Subject[]>(["catalog", "subjects"]) ?? client.getQueryData<Subject[]>(["catalog", "subjects", "all"]);
  return lists?.find((s) => s.id === subjectId)?.name;
}

function cachedTopicName(client: QueryClient, subjectId: string, grade: number, topicId: string): string | undefined {
  const list =
    client.getQueryData<Topic[]>(["catalog", "topics", subjectId, grade]) ??
    client.getQueryData<Topic[]>(["catalog", "topics", subjectId]);
  return list?.find((t) => t.id === topicId)?.name;
}

function unwrap<T>(result: { data?: T; error?: unknown }): T {
  if (result.error || result.data === undefined) throw new Error(getApiErrorMessage(result.error));
  return result.data;
}

type TodoGroupData = {
  key: string;
  title: string;
  href: string;
  enabled: boolean;
  isLoading: boolean;
  isError: boolean;
  refetch: () => unknown;
  rows: ReactNode[];
  total?: number;
};

function GroupView({ group }: { group: TodoGroupData }) {
  const headingId = useId();
  return (
    <section aria-labelledby={headingId}>
      <div className="mb-2 flex flex-wrap items-baseline justify-between gap-x-4 gap-y-1">
        <div className="flex items-baseline gap-2">
          <h2 id={headingId} className="text-[15px] font-semibold">
            {group.title}
          </h2>
          {group.total !== undefined ? (
            <span className="text-[12.5px] text-muted tabular-nums">{vi.home.total(group.total)}</span>
          ) : null}
        </div>
        <Link href={group.href} className="text-[12.5px] font-semibold text-accent-strong underline underline-offset-2">
          {vi.home.viewAll}
        </Link>
      </div>
      {group.isError ? (
        <Notice
          tone="error"
          action={
            <Button variant="secondary" size="sm" onClick={() => void group.refetch()}>
              {vi.home.retry}
            </Button>
          }
        >
          {vi.home.loadFailed}
        </Notice>
      ) : (
        <div>{group.rows}</div>
      )}
    </section>
  );
}

/** "Cần bạn xử lý": one request per group, up to five rows each, failures isolated per group. */
export function TodoSection({ role }: { role: Role }) {
  const client = useQueryClient();
  const isAuthor = role === "teacher" || role === "head";
  const isReviewer = role === "head" || role === "admin";
  const query = { refetchOnMount: "always" } as const;

  const rejectedQuestionsQuery = useQuery({
    ...query,
    queryKey: ["home", "rejectedQuestions"],
    enabled: isAuthor,
    queryFn: async () =>
      unwrap(
        await api.GET("/questions", { params: { query: { status: "Rejected", mine: true, page: 1, pageSize: MAX_ROWS } } }),
      ),
  });

  const unreviewedAiQuery = useQuery({
    ...query,
    queryKey: ["home", "unreviewedAi"],
    enabled: isAuthor,
    queryFn: async () =>
      unwrap(
        await api.GET("/questions", { params: { query: { status: "Draft", mine: true, page: 1, pageSize: DRAFT_PAGE_SIZE } } }),
      ),
    select: (data) => ({ groups: groupUnreviewedAiQuestions(data.items), totalItems: data.totalItems }),
  });

  const aiJobsQuery = useQuery({
    ...query,
    queryKey: ["home", "aiJobs"],
    enabled: isAuthor,
    queryFn: async () => unwrap(await api.GET("/ai/jobs", { params: { query: { page: 1, pageSize: 20 } } })),
    select: (data) => attentionJobs(data.items, new Date()),
  });

  const reviewQuestionsQuery = useQuery({
    ...query,
    queryKey: ["home", "reviewQuestions"],
    enabled: isReviewer,
    queryFn: async () =>
      unwrap(await api.GET("/review/questions", { params: { query: { page: 1, pageSize: MAX_ROWS } } })),
  });

  const reviewExamsQuery = useQuery({
    ...query,
    queryKey: ["home", "reviewExams"],
    enabled: isReviewer,
    queryFn: async () =>
      unwrap(await api.GET("/review/exam-versions", { params: { query: { page: 1, pageSize: MAX_ROWS } } })),
  });

  const rejectedExamsQuery = useQuery({
    ...query,
    queryKey: ["home", "rejectedExams"],
    enabled: isAuthor,
    queryFn: async () => unwrap(await api.GET("/exams", { params: { query: { mine: true, page: 1, pageSize: 100 } } })),
    select: (data) => rejectedExams(data.items),
  });

  const base = (q: { isLoading: boolean; isError: boolean; refetch: () => unknown }, enabled: boolean) => ({
    enabled,
    isLoading: q.isLoading,
    isError: q.isError,
    refetch: q.refetch,
  });

  const groups: TodoGroupData[] = [
    {
      ...base(rejectedQuestionsQuery, isAuthor),
      key: "rejectedQuestions",
      title: vi.home.rejectedQuestions,
      href: "/questions/",
      rows: (rejectedQuestionsQuery.data?.items ?? []).map((item) => (
        <ListRow
          key={item.id}
          tone={questionTone("Rejected")}
          href={`/questions/detail/?id=${item.id}`}
          title={
            <div className="line-clamp-2 font-normal">
              <MathMarkdown links={false}>{item.stem}</MathMarkdown>
            </div>
          }
          reason={item.reviewNote ? `${vi.questions.rejectedReason}: ${item.reviewNote}` : undefined}
          end={<StatusText tone={questionTone("Rejected")}>{vi.questions.statuses.Rejected}</StatusText>}
        />
      )),
    },
    {
      ...base(unreviewedAiQuery, isAuthor),
      key: "unreviewedAi",
      title: vi.home.unreviewedAi,
      href: "/ai/",
      rows: (unreviewedAiQuery.data?.groups ?? []).map((group) => {
        const subject = cachedSubjectName(client, group.subjectId);
        const topic = cachedTopicName(client, group.subjectId, group.grade, group.topicId);
        const title = subject
          ? [subject, `${vi.common.gradePrefix} ${group.grade}`, topic].filter(Boolean).join(", ")
          : vi.home.aiJobFallback(formatDate(group.firstCreatedAt) ?? "");
        const atLeast = (unreviewedAiQuery.data?.totalItems ?? 0) > DRAFT_PAGE_SIZE;
        return (
          <ListRow
            key={group.aiJobId}
            tone="draft"
            href={`/ai/job/?id=${group.aiJobId}`}
            title={title}
            meta={vi.home.unreviewedCount(group.count, atLeast)}
            end={<StatusText tone="draft">{vi.questions.aiNotReviewed}</StatusText>}
          />
        );
      }),
    },
    {
      ...base(aiJobsQuery, isAuthor),
      key: "aiJobs",
      title: vi.home.aiJobs,
      href: "/ai/",
      rows: (aiJobsQuery.data ?? []).map((job) => {
        const tone = aiJobTone(job.status);
        const subject = cachedSubjectName(client, job.params.subjectId);
        const gradeLabel = `${vi.common.gradePrefix} ${job.params.grade}`;
        const sentAt = formatDateTime(job.createdAt) ?? "";
        return (
          <ListRow
            key={job.id}
            tone={tone}
            href={`/ai/job/?id=${job.id}`}
            title={subject ? `${subject}, ${gradeLabel}` : vi.home.aiJobFallback(formatDate(job.createdAt) ?? "")}
            meta={vi.ai.jobMeta(
              vi.questions.types[job.params.type],
              vi.questions.difficulties[job.params.difficulty],
              job.params.count,
              sentAt,
            )}
            reason={job.status === "Failed" ? job.errorMessage : undefined}
            end={
              <StatusText tone={tone} className="inline-flex items-center gap-1.5">
                {job.status === "Running" && <RunningDot />}
                {vi.ai.jobStatuses[job.status]}
              </StatusText>
            }
          />
        );
      }),
    },
    {
      ...base(reviewQuestionsQuery, isReviewer),
      key: "reviewQuestions",
      title: vi.home.reviewQuestions,
      href: "/review/",
      total: reviewQuestionsQuery.data?.totalItems,
      rows: (reviewQuestionsQuery.data?.items ?? []).map((item) => (
        <ListRow
          key={item.id}
          tone={questionTone("Submitted")}
          href={`/review/question/?id=${item.id}`}
          title={
            <div className="line-clamp-2 font-normal">
              <MathMarkdown links={false}>{item.stem}</MathMarkdown>
            </div>
          }
          meta={vi.review.sentBy(item.createdBy.fullName, formatDateTime(item.submittedAt))}
          end={<StatusText tone={questionTone("Submitted")}>{vi.questions.statuses.Submitted}</StatusText>}
        />
      )),
    },
    {
      ...base(reviewExamsQuery, isReviewer),
      key: "reviewExams",
      title: vi.home.reviewExams,
      href: "/review/?tab=exams",
      total: reviewExamsQuery.data?.totalItems,
      rows: (reviewExamsQuery.data?.items ?? []).map((item) => (
        <ListRow
          key={item.id}
          tone={examVersionTone("Submitted")}
          href={`/review/exam/?id=${item.id}`}
          title={vi.home.rejectedExamRow(item.examTitle, item.versionNo)}
          meta={vi.review.sentBy(item.createdBy.fullName, formatDateTime(item.submittedAt))}
          end={<StatusText tone={examVersionTone("Submitted")}>{vi.review.examStatuses.Submitted}</StatusText>}
        />
      )),
    },
    {
      ...base(rejectedExamsQuery, isAuthor),
      key: "rejectedExams",
      title: vi.home.rejectedExams,
      href: "/exams/",
      rows: (rejectedExamsQuery.data ?? []).map((exam) => (
        <ListRow
          key={exam.id}
          tone={examVersionTone("Rejected")}
          href={`/exams/view/?id=${exam.id}&vid=${exam.latestVersionId}`}
          title={vi.home.rejectedExamRow(exam.title, exam.latestVersionNo)}
          reason={vi.home.rejectedExamHint}
          end={<StatusText tone={examVersionTone("Rejected")}>{vi.review.examStatuses.Rejected}</StatusText>}
        />
      )),
    },
  ];

  const active = groups.filter((g) => g.enabled);
  const visible = active.filter((g) => g.isError || g.rows.length > 0);
  const settled = active.every((g) => !g.isLoading);

  return (
    <section>
      <h2 className="mb-3 text-[17px] font-semibold">{vi.home.todoTitle}</h2>
      <div className="space-y-6">
        {visible.map((group) => (
          <GroupView key={group.key} group={{ ...group, rows: group.rows.slice(0, MAX_ROWS) }} />
        ))}
      </div>
      {visible.length === 0 && !settled ? <p className="text-[13.5px] text-muted">{vi.common.loading}</p> : null}
      {visible.length === 0 && settled ? (
        <p className="rounded-lg border border-line bg-surface px-4 py-5 text-[13.5px] text-muted">{vi.home.nothingToDo}</p>
      ) : null}
    </section>
  );
}
