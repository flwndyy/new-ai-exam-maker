"use client";

import { useQuery } from "@tanstack/react-query";
import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { Suspense, useEffect, useState } from "react";

import { MathMarkdown } from "@/components/math-markdown";
import { PageHeader } from "@/components/shell/page-header";
import { ButtonLink } from "@/components/ui/button";
import { EmptyState } from "@/components/ui/empty-state";
import { Checkbox } from "@/components/ui/field";
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
import { questionTone } from "@/lib/ui/status";
import { vi } from "@/messages/vi";

type QuestionStatus = components["schemas"]["QuestionStatus"];
type QuestionType = components["schemas"]["QuestionType"];
type Difficulty = components["schemas"]["Difficulty"];
type Grade = components["schemas"]["Grade"];
type AiVerification = components["schemas"]["AiVerification"];

const STATUSES: QuestionStatus[] = ["Draft", "Submitted", "Approved", "Rejected", "Archived"];
const TYPES: QuestionType[] = ["MultipleChoice", "TrueFalse", "ShortAnswer", "Essay"];
const DIFFICULTIES: Difficulty[] = ["Recognition", "Understanding", "Application", "HighApplication"];
const GRADES: Grade[] = [10, 11, 12];
const AI_FLAG_TONE: Record<AiVerification, TagTone> = { NotChecked: "neutral", Agree: "ok", Disagree: "rej" };

function QuestionsPageInner() {
  const { me } = useAuth();
  const router = useRouter();
  const searchParams = useSearchParams();
  const deletedParam = searchParams.get("deleted") === "1";
  const [showDeletedNotice, setShowDeletedNotice] = useState(deletedParam);
  const [page, setPage] = useState(1);

  useEffect(() => {
    if (deletedParam) {
      router.replace("/questions/");
    }
  }, [deletedParam, router]);
  const [status, setStatus] = useState<string>("");
  const [subjectId, setSubjectId] = useState<string>("");
  const [topicId, setTopicId] = useState<string>("");
  const [grade, setGrade] = useState<string>("");
  const [type, setType] = useState<string>("");
  const [difficulty, setDifficulty] = useState<string>("");
  const [mine, setMine] = useState(false);

  const canCreate = me?.role === "teacher" || me?.role === "head";

  const subjectsQuery = useQuery({
    queryKey: ["catalog", "subjects"],
    queryFn: () => fetchAllSubjects(),
  });

  const topicsQuery = useQuery({
    queryKey: ["catalog", "topics", subjectId, grade],
    enabled: Boolean(subjectId),
    queryFn: () => fetchAllTopics(subjectId, grade ? (Number(grade) as Grade) : undefined),
  });

  const questionsQuery = useQuery({
    queryKey: ["questions", page, status, subjectId, topicId, grade, type, difficulty, mine],
    queryFn: async () => {
      const { data, error } = await api.GET("/questions", {
        params: {
          query: {
            page,
            pageSize: 20,
            status: (status || undefined) as QuestionStatus | undefined,
            subjectId: subjectId || undefined,
            topicId: topicId || undefined,
            grade: grade ? (Number(grade) as Grade) : undefined,
            type: (type || undefined) as QuestionType | undefined,
            difficulty: (difficulty || undefined) as Difficulty | undefined,
            mine: mine || undefined,
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

  // Chapter names are only known once a subject is chosen; without one the meta line omits the chapter.
  const topicNames = new Map((topicsQuery.data ?? []).map((t) => [t.id, t.name] as const));

  return (
    <>
      <PageHeader
        title={vi.questions.title}
        actions={
          canCreate ? (
            <ButtonLink href="/questions/detail/" variant="boardPrimary">
              {vi.questions.createButton}
            </ButtonLink>
          ) : null
        }
      />
      <div className="px-4 py-5 sm:px-7 sm:py-6">
        {showDeletedNotice && (
          <Notice
            tone="success"
            className="mb-3.5"
            action={
              <button
                type="button"
                aria-label={vi.common.cancel}
                onClick={() => setShowDeletedNotice(false)}
                className="px-1 text-base leading-none font-semibold underline"
              >
                ×
              </button>
            }
          >
            {vi.questions.deletedSuccess}
          </Notice>
        )}

        <FilterBar>
          <FilterSelect
            label={vi.common.status}
            value={status}
            onChange={(e) => {
              setStatus(e.target.value);
              setPage(1);
            }}
          >
            <option value="">{vi.common.status}: {vi.common.all}</option>
            {STATUSES.map((s) => (
              <option key={s} value={s}>{vi.questions.statuses[s]}</option>
            ))}
          </FilterSelect>

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

          <FilterSelect
            label={vi.questions.grade}
            value={grade}
            onChange={(e) => {
              setGrade(e.target.value);
              setTopicId("");
              setPage(1);
            }}
          >
            <option value="">{vi.questions.grade}: {vi.common.all}</option>
            {GRADES.map((g) => (
              <option key={g} value={g}>{vi.common.gradePrefix} {g}</option>
            ))}
          </FilterSelect>

          <FilterSelect
            label={vi.questions.type}
            value={type}
            onChange={(e) => {
              setType(e.target.value);
              setPage(1);
            }}
          >
            <option value="">{vi.questions.type}: {vi.common.all}</option>
            {TYPES.map((t) => (
              <option key={t} value={t}>{vi.questions.types[t]}</option>
            ))}
          </FilterSelect>

          <FilterSelect
            label={vi.questions.difficulty}
            value={difficulty}
            onChange={(e) => {
              setDifficulty(e.target.value);
              setPage(1);
            }}
          >
            <option value="">{vi.questions.difficulty}: {vi.common.all}</option>
            {DIFFICULTIES.map((d) => (
              <option key={d} value={d}>{vi.questions.difficulties[d]}</option>
            ))}
          </FilterSelect>

          <Checkbox
            label={vi.questions.mineOnly}
            checked={mine}
            onChange={(e) => {
              setMine(e.target.checked);
              setPage(1);
            }}
          />
        </FilterBar>

        {questionsQuery.isLoading && <EmptyState busy title={vi.common.loading} />}
        {questionsQuery.isError && <Notice tone="error">{getUiErrorMessage(questionsQuery.error)}</Notice>}

        {questionsQuery.data && questionsQuery.data.items.length === 0 && (
          <EmptyState title={vi.questions.emptyTitle} description={vi.common.empty} />
        )}

        {questionsQuery.data && questionsQuery.data.items.length > 0 && (
          <div>
            {questionsQuery.data.items.map((item) => {
              const isItemEditable =
                canCreate &&
                item.createdBy.id === me?.id &&
                (item.status === "Draft" || item.status === "Rejected" || item.status === "Approved");
              const isAi = item.source === "AI";
              const needsReview =
                isAi && !item.aiReviewedByAuthor && (item.status === "Draft" || item.status === "Rejected");
              const tone = questionTone(item.status);
              return (
                <ListRow
                  key={item.id}
                  tone={tone}
                  href={`/questions/detail/?id=${item.id}`}
                  title={
                    <div className="line-clamp-2 font-normal">
                      <MathMarkdown links={false}>{item.stem}</MathMarkdown>
                    </div>
                  }
                  meta={
                    <>
                      {isAi && (
                        <span className="mr-2 inline-flex flex-wrap gap-1.5 align-middle">
                          <Tag tone="ai">{vi.questions.aiSource}</Tag>
                          <Tag tone={AI_FLAG_TONE[item.aiVerification]}>{vi.questions.aiFlags[item.aiVerification]}</Tag>
                        </span>
                      )}
                      {vi.questions.metaLine(
                        vi.questions.types[item.type],
                        vi.questions.difficulties[item.difficulty],
                        item.grade,
                        topicNames.get(item.topicId),
                      )}
                    </>
                  }
                  reason={
                    item.status === "Rejected" && item.reviewNote
                      ? `${vi.questions.rejectedReason}: ${item.reviewNote}`
                      : undefined
                  }
                  end={
                    <>
                      <StatusText tone={tone}>{vi.questions.statuses[item.status]}</StatusText>
                      {needsReview && <span className="text-xs text-muted">{vi.questions.aiNotReviewed}</span>}
                      <Link
                        href={`/questions/detail/?id=${item.id}`}
                        className="text-[12.5px] font-semibold text-accent-strong underline underline-offset-2"
                      >
                        {isItemEditable ? vi.common.edit : vi.common.view}
                      </Link>
                    </>
                  }
                />
              );
            })}
          </div>
        )}

        {questionsQuery.data && (questionsQuery.data.items.length > 0 || page > 1) && (
          <Pager page={page} totalPages={Math.max(1, questionsQuery.data.totalPages)} onPage={setPage} />
        )}
      </div>
    </>
  );
}

export default function QuestionsPage() {
  return (
    <Suspense fallback={<EmptyState busy title={vi.common.loading} />}>
      <QuestionsPageInner />
    </Suspense>
  );
}
