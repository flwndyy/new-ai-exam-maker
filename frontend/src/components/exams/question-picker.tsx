"use client";

import { useQuery } from "@tanstack/react-query";
import { useState } from "react";

import { MathMarkdown } from "@/components/math-markdown";
import { fetchAllTopics } from "@/lib/api/catalog";
import { api } from "@/lib/api/client";
import { getUiErrorMessage } from "@/lib/api/error";
import type { components } from "@/lib/api/schema";
import { isSelectable } from "@/lib/exams/rules";
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
  const selectClass = "rounded border border-slate-300 bg-white px-2 py-1.5 text-sm";

  return (
    <div className="space-y-3">
      <h2 className="text-lg font-semibold text-slate-900">{vi.exams.bankTitle}</h2>
      <div className="grid grid-cols-1 gap-2 sm:grid-cols-3">
        <select aria-label={vi.questions.topic} value={topicId} onChange={(e) => resetPage(setTopicId)(e.target.value)} className={selectClass}>
          <option value="">{vi.exams.allTopics}</option>
          {topicsQuery.data?.map((t) => (
            <option key={t.id} value={t.id}>
              {t.name}
            </option>
          ))}
        </select>
        <select
          aria-label={vi.questions.type}
          value={type}
          onChange={(e) => resetPage(setType)(e.target.value as QuestionType | "")}
          className={selectClass}
        >
          <option value="">{vi.exams.allTypes}</option>
          {TYPES.map((t) => (
            <option key={t} value={t}>
              {vi.questions.types[t]}
            </option>
          ))}
        </select>
        <select
          aria-label={vi.questions.difficulty}
          value={difficulty}
          onChange={(e) => resetPage(setDifficulty)(e.target.value as Difficulty | "")}
          className={selectClass}
        >
          <option value="">{vi.exams.allDifficulties}</option>
          {DIFFICULTIES.map((d) => (
            <option key={d} value={d}>
              {vi.questions.difficulties[d]}
            </option>
          ))}
        </select>
        <label className="flex items-center gap-2 text-sm text-slate-700 sm:col-span-3">
          <input type="checkbox" checked={mine} onChange={(e) => resetPage(setMine)(e.target.checked)} />
          {vi.exams.mineQuestionsOnly}
        </label>
      </div>

      {questionsQuery.isLoading && <p className="text-sm text-slate-600">{vi.common.loading}</p>}
      {questionsQuery.isError && <p className="text-sm text-rose-600">{getUiErrorMessage(questionsQuery.error)}</p>}
      {questionsQuery.data?.items.length === 0 && <p className="text-sm text-slate-500">{vi.exams.noBankResults}</p>}

      <ul className="space-y-2">
        {questionsQuery.data?.items.map((q) => {
          const canAdd = isSelectable(q, meId) && !selectedIds.has(q.id);
          return (
            <li key={q.id} className="rounded border border-slate-200 bg-white p-3">
              <div className="flex flex-wrap items-center gap-2 text-xs">
                <span className="rounded bg-slate-100 px-2 py-0.5">{vi.questions.statuses[q.status]}</span>
                <span className="text-slate-500">{vi.questions.types[q.type]}</span>
                <span className="text-slate-500">{vi.questions.difficulties[q.difficulty]}</span>
              </div>
              <div className="prose prose-sm mt-1 line-clamp-3 max-w-none">
                <MathMarkdown>{q.stem}</MathMarkdown>
              </div>
              <div className="mt-2 flex items-center justify-between gap-2">
                {canAdd && q.status !== "Approved" ? <span className="text-xs text-amber-700">{vi.exams.needsApproval}</span> : <span />}
                {canAdd && (
                  <button
                    type="button"
                    disabled={addDisabled}
                    onClick={() => onAdd(q)}
                    className="rounded border border-slate-300 px-3 py-1 text-sm font-medium hover:bg-slate-50 disabled:opacity-40"
                  >
                    {vi.exams.add}
                  </button>
                )}
              </div>
            </li>
          );
        })}
      </ul>

      {questionsQuery.data && questionsQuery.data.totalPages > 1 && (
        <div className="flex items-center justify-center gap-3 text-sm">
          <button type="button" disabled={page <= 1} onClick={() => setPage((p) => p - 1)} className="rounded border px-3 py-1 disabled:opacity-40">
            {vi.exams.prevPage}
          </button>
          <span>{vi.exams.pageInfo(page, questionsQuery.data.totalPages)}</span>
          <button
            type="button"
            disabled={page >= questionsQuery.data.totalPages}
            onClick={() => setPage((p) => p + 1)}
            className="rounded border px-3 py-1 disabled:opacity-40"
          >
            {vi.exams.nextPage}
          </button>
        </div>
      )}
    </div>
  );
}
