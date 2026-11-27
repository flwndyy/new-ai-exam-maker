"use client";

import { useQuery } from "@tanstack/react-query";
import { useState } from "react";

import { MathMarkdown } from "@/components/math-markdown";
import { Button } from "@/components/ui/button";
import { EmptyState } from "@/components/ui/empty-state";
import { Checkbox } from "@/components/ui/field";
import { FilterBar, FilterSelect } from "@/components/ui/filter-bar";
import { ListRow } from "@/components/ui/list-row";
import { Notice } from "@/components/ui/notice";
import { Pager } from "@/components/ui/pager";
import { StatusText } from "@/components/ui/status-text";
import { fetchAllTopics } from "@/lib/api/catalog";
import { api } from "@/lib/api/client";
import { getUiErrorMessage } from "@/lib/api/error";
import type { components } from "@/lib/api/schema";
import { isSelectable } from "@/lib/exams/rules";
import { questionTone } from "@/lib/ui/status";
import { vi } from "@/messages/vi";

type Question = components["schemas"]["Question"];
type QuestionType = components["schemas"]["QuestionType"];
type Difficulty = components["schemas"]["Difficulty"];
type Grade = components["schemas"]["Grade"];

const PAGE_SIZE = 20;
const TYPES: QuestionType[] = ["MultipleChoice", "TrueFalse", "ShortAnswer", "Essay"];
const DIFFICULTIES: Difficulty[] = ["Recognition", "Understanding", "Application", "HighApplication"];

/** Bank column of the composer: questions of the exam's subject/grade the author may add. */
export function QuestionPicker({
  subjectId,
  grade,
  meId,
  selectedIds,
  addDisabled = false,
  onAdd,
}: {
  subjectId: string;
  grade: Grade;
  meId: string;
  selectedIds: ReadonlySet<string>;
  /** Temporarily disables "Thêm" (e.g. while the composer is saving). */
  addDisabled?: boolean;
  onAdd: (q: Question) => void;
}) {
  const [page, setPage] = useState(1);
  const [topicId, setTopicId] = useState("");
  const [type, setType] = useState<QuestionType | "">("");
  const [difficulty, setDifficulty] = useState<Difficulty | "">("");
  const [mine, setMine] = useState(false);

  const topicsQuery = useQuery({
    queryKey: ["catalog", "topics", subjectId, grade],
    queryFn: () => fetchAllTopics(subjectId, grade),
  });

  const filters = {
    page,
    pageSize: PAGE_SIZE,
    subjectId,
    grade,
    topicId: topicId || undefined,
    type: type || undefined,
    difficulty: difficulty || undefined,
    mine: mine || undefined,
  };
  const questionsQuery = useQuery({
    queryKey: ["questionsPicker", filters],
    queryFn: async () => {
      const { data, error } = await api.GET("/questions", { params: { query: filters } });
      if (error || !data) throw error ?? new Error(vi.common.requestFailed);
      return data;
    },
  });

  const resetPage = <T,>(set: (v: T) => void) => (v: T) => {
    set(v);
    setPage(1);
  };

  return (
    <div className="min-w-0">
      <h2 className="mb-2.5 flex items-baseline gap-2.5 text-[15px] font-semibold">{vi.exams.bankTitle}</h2>
      <FilterBar>
        <FilterSelect label={vi.questions.topic} value={topicId} onChange={(e) => resetPage(setTopicId)(e.target.value)}>
          <option value="">{vi.exams.allTopics}</option>
          {topicsQuery.data?.map((t) => (
            <option key={t.id} value={t.id}>
              {t.name}
            </option>
          ))}
        </FilterSelect>
        <FilterSelect label={vi.questions.type} value={type} onChange={(e) => resetPage(setType)(e.target.value as QuestionType | "")}>
          <option value="">{vi.exams.allTypes}</option>
          {TYPES.map((t) => (
            <option key={t} value={t}>
              {vi.questions.types[t]}
            </option>
          ))}
        </FilterSelect>
        <FilterSelect
          label={vi.questions.difficulty}
          value={difficulty}
          onChange={(e) => resetPage(setDifficulty)(e.target.value as Difficulty | "")}
        >
          <option value="">{vi.exams.allDifficulties}</option>
          {DIFFICULTIES.map((d) => (
            <option key={d} value={d}>
              {vi.questions.difficulties[d]}
            </option>
          ))}
        </FilterSelect>
        <Checkbox label={vi.exams.mineQuestionsOnly} checked={mine} onChange={(e) => resetPage(setMine)(e.target.checked)} />
      </FilterBar>

      {questionsQuery.isLoading && <EmptyState busy title={vi.common.loading} className="min-h-[120px]" />}
      {questionsQuery.isError && <Notice tone="error">{getUiErrorMessage(questionsQuery.error)}</Notice>}
      {questionsQuery.data?.items.length === 0 && <EmptyState title={vi.exams.noBankResults} className="min-h-[120px]" />}

      <ul>
        {questionsQuery.data?.items.map((q) => {
          const picked = selectedIds.has(q.id);
          const canAdd = isSelectable(q, meId) && !picked;
          return (
            <li key={q.id}>
              <ListRow
                tone={questionTone(q.status)}
                title={
                  <div className="line-clamp-3 font-normal">
                    <MathMarkdown links={false}>{q.stem}</MathMarkdown>
                  </div>
                }
                meta={
                  <>
                    <div>{`${vi.questions.types[q.type]}, ${vi.questions.difficulties[q.difficulty]}`}</div>
                    {canAdd && q.status !== "Approved" && <div className="mt-1 text-wait-text">{vi.exams.needsApproval}</div>}
                  </>
                }
                end={
                  <>
                    <StatusText tone={questionTone(q.status)}>{vi.questions.statuses[q.status]}</StatusText>
                    {canAdd && (
                      <Button variant="secondary" size="sm" disabled={addDisabled} onClick={() => onAdd(q)}>
                        {vi.exams.add}
                      </Button>
                    )}
                    {picked && <span className="text-[12.5px] text-muted">{vi.exams.alreadyInExam}</span>}
                  </>
                }
              />
            </li>
          );
        })}
      </ul>

      {questionsQuery.data && questionsQuery.data.totalPages > 1 && (
        <Pager page={page} totalPages={questionsQuery.data.totalPages} onPage={setPage} />
      )}
    </div>
  );
}
