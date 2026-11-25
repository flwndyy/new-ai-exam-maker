"use client";

import { useQuery } from "@tanstack/react-query";
import { useSearchParams } from "next/navigation";
import { Suspense, useState } from "react";

import { MathMarkdown } from "@/components/math-markdown";
import { PageHeader } from "@/components/shell/page-header";
import { BoardTabs } from "@/components/ui/board-tabs";
import { ButtonLink } from "@/components/ui/button";
import { EmptyState } from "@/components/ui/empty-state";
import { FilterBar, FilterSelect } from "@/components/ui/filter-bar";
import { ListRow } from "@/components/ui/list-row";
import { Notice } from "@/components/ui/notice";
import { Pager } from "@/components/ui/pager";
import { StatusText } from "@/components/ui/status-text";
import { Tag, type TagTone } from "@/components/ui/tag";
import { fetchAllSubjects, fetchAllTopics } from "@/lib/api/catalog";
import { api } from "@/lib/api/client";
import { getApiErrorMessage, getUiErrorMessage } from "@/lib/api/error";
import type { components } from "@/lib/api/schema";
import { useAuth } from "@/lib/auth/auth-context";
import { formatDecimalForInput } from "@/lib/format/decimal";
import { examVersionTone, questionTone } from "@/lib/ui/status";
import { vi } from "@/messages/vi";

type AiVerification = components["schemas"]["AiVerification"];

const AI_FLAG_TONE: Record<AiVerification, TagTone> = { NotChecked: "neutral", Agree: "ok", Disagree: "rej" };

const dateFormat = new Intl.DateTimeFormat("vi-VN", {
  dateStyle: "short",
  timeStyle: "short",
  timeZone: "Asia/Ho_Chi_Minh",
});

const BODY = "px-4 py-5 sm:px-7 sm:py-6";

function formatSentAt(value?: string | null): string | null {
  if (!value) return null;
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? null : dateFormat.format(date);
}

function ReviewQueueContent() {
  const { me } = useAuth();
  const searchParams = useSearchParams();
  // `?tab=exams` only picks the initial tab (e.g. the home page link); clicking tabs works as before.
  const [tab, setTab] = useState<"questions" | "exams">(searchParams.get("tab") === "exams" ? "exams" : "questions");
  const [page, setPage] = useState(1);
  const [subjectId, setSubjectId] = useState("");
  const [topicId, setTopicId] = useState("");

  const canReview = me?.role === "head" || me?.role === "admin";

  const subjectsQuery = useQuery({
    queryKey: ["catalog", "subjects"],
    enabled: canReview,
    queryFn: () => fetchAllSubjects(),
  });

  const topicsQuery = useQuery({
    queryKey: ["catalog", "topics", subjectId],
    enabled: canReview && tab === "questions" && Boolean(subjectId),
    queryFn: () => fetchAllTopics(subjectId),
  });

  const questionsQuery = useQuery({
    queryKey: ["review", "questions", page, subjectId, topicId],
    enabled: canReview && tab === "questions",
    queryFn: async () => {
      const { data, error } = await api.GET("/review/questions", {
        params: {
          query: {
            page,
            pageSize: 20,
            subjectId: subjectId || undefined,
            topicId: topicId || undefined,
          },
        },
      });
      if (error) throw new Error(getApiErrorMessage(error));
      if (page > 1 && data && data.items.length === 0) {
        setPage(Math.max(1, data.totalPages || page - 1));
      }
      return data;
    },
  });

  const examsQuery = useQuery({
    queryKey: ["review", "exams", page, subjectId],
    enabled: canReview && tab === "exams",
    queryFn: async () => {
      const { data, error } = await api.GET("/review/exam-versions", {
        params: {
          query: {
            page,
            pageSize: 20,
            subjectId: subjectId || undefined,
          },
        },
      });
      if (error) throw new Error(getApiErrorMessage(error));
      if (page > 1 && data && data.items.length === 0) {
        setPage(Math.max(1, data.totalPages || page - 1));
      }
      return data;
    },
  });

  if (!canReview) {
    return (
      <>
        <PageHeader title={vi.review.title} />
        <div className={BODY}>
          <Notice tone="error">{vi.common.forbidden}</Notice>
        </div>
      </>
    );
  }

  const activeData = tab === "questions" ? questionsQuery.data : examsQuery.data;
  const isLoading = tab === "questions" ? questionsQuery.isLoading : examsQuery.isLoading;
  const error = tab === "questions" ? questionsQuery.error : examsQuery.error;

  // Only tabs whose list was loaded show a count; the other tab keeps its plain label until it is opened.
  const tabLabel = (label: string, totalItems?: number) =>
    totalItems === undefined ? label : vi.review.tabWithCount(label, totalItems);

  return (
    <>
      <PageHeader
        title={vi.review.title}
        description={vi.review.queueDescription}
        tabs={
          <BoardTabs
            className="px-0! sm:px-0!"
            active={tab}
            onSelect={(key) => {
              if (key === "questions") {
                setTab("questions");
                setPage(1);
              } else {
                setTab("exams");
                setTopicId("");
                setPage(1);
              }
            }}
            items={[
              { key: "questions", label: tabLabel(vi.review.questionsTab, questionsQuery.data?.totalItems) },
              { key: "exams", label: tabLabel(vi.review.examsTab, examsQuery.data?.totalItems) },
            ]}
          />
        }
      />
      <div className={BODY}>
        <FilterBar>
          <FilterSelect
            label={vi.questions.subject}
            value={subjectId}
            onChange={(e) => {
              setSubjectId(e.target.value);
              setTopicId("");
              setPage(1);
            }}
          >
            <option value="">{vi.questions.subject}: {vi.common.all}</option>
            {subjectsQuery.data?.map((s) => (
              <option key={s.id} value={s.id}>{s.name}</option>
            ))}
          </FilterSelect>

          {tab === "questions" && (
            <FilterSelect
              label={vi.questions.topic}
              disabled={!subjectId}
              value={topicId}
              onChange={(e) => {
                setTopicId(e.target.value);
                setPage(1);
              }}
              className="disabled:opacity-60"
            >
              <option value="">{vi.questions.topic}: {vi.common.all}</option>
              {topicsQuery.data?.map((t) => (
                <option key={t.id} value={t.id}>{t.name}</option>
              ))}
            </FilterSelect>
          )}
        </FilterBar>

        {isLoading && <EmptyState busy title={vi.common.loading} />}
        {error && <Notice tone="error">{getUiErrorMessage(error)}</Notice>}

        {activeData && activeData.items.length === 0 && (
          <EmptyState title={vi.review.emptyTitle} description={vi.common.empty} />
        )}

        {tab === "questions" && questionsQuery.data && questionsQuery.data.items.length > 0 && (
          <div>
            {questionsQuery.data.items.map((item) => (
              <ListRow
                key={item.id}
                tone={questionTone("Submitted")}
                href={`/review/question/?id=${item.id}`}
                title={
                  <div className="line-clamp-2 font-normal">
                    <MathMarkdown links={false}>{item.stem}</MathMarkdown>
                  </div>
                }
                meta={
                  <>
                    {item.source === "AI" && (
                      <span className="mr-2 inline-flex flex-wrap gap-1.5 align-middle">
                        <Tag tone="ai">{vi.questions.aiSource}</Tag>
                        <Tag tone={AI_FLAG_TONE[item.aiVerification]}>{vi.questions.aiFlags[item.aiVerification]}</Tag>
                      </span>
                    )}
                    {vi.review.queueQuestionMeta(
                      vi.questions.types[item.type],
                      vi.questions.difficulties[item.difficulty],
                      vi.review.sentBy(item.createdBy.fullName, formatSentAt(item.submittedAt)),
                    )}
                  </>
                }
                end={
                  <>
                    <StatusText tone={questionTone("Submitted")}>{vi.questions.statuses.Submitted}</StatusText>
                    <ButtonLink href={`/review/question/?id=${item.id}`} variant="secondary" size="sm">
                      {vi.review.openReview}
                    </ButtonLink>
                  </>
                }
              />
            ))}
          </div>
        )}

        {tab === "exams" && examsQuery.data && examsQuery.data.items.length > 0 && (
          <div>
            {examsQuery.data.items.map((item) => (
              <ListRow
                key={item.id}
                tone={examVersionTone("Submitted")}
                href={`/review/exam/?id=${item.id}`}
                title={`${item.examTitle} (v${item.versionNo})`}
                meta={vi.review.queueExamMeta(
                  item.grade,
                  item.questionCount,
                  formatDecimalForInput(item.totalPoints),
                  vi.review.sentBy(item.createdBy.fullName, formatSentAt(item.submittedAt)),
                )}
                end={
                  <>
                    <StatusText tone={examVersionTone("Submitted")}>{vi.review.examStatuses.Submitted}</StatusText>
                    <ButtonLink href={`/review/exam/?id=${item.id}`} variant="secondary" size="sm">
                      {vi.review.openReview}
                    </ButtonLink>
                  </>
                }
              />
            ))}
          </div>
        )}

        {activeData && (activeData.items.length > 0 || page > 1) && (
          <Pager page={page} totalPages={Math.max(1, activeData.totalPages)} onPage={setPage} />
        )}
      </div>
    </>
  );
}

export default function ReviewQueuePage() {
  return (
    <Suspense
      fallback={
        <>
          <PageHeader title={vi.review.title} />
          <div className={BODY}>
            <EmptyState busy title={vi.common.loading} />
          </div>
        </>
      }
    >
      <ReviewQueueContent />
    </Suspense>
  );
}
