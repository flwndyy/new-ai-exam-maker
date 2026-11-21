"use client";

import { useQuery } from "@tanstack/react-query";
import Link from "next/link";
import { useState } from "react";

import { MathMarkdown } from "@/components/math-markdown";
import { api } from "@/lib/api/client";
import { getApiErrorMessage } from "@/lib/api/error";
import type { components } from "@/lib/api/schema";
import { vi } from "@/messages/vi";

type QuestionStatus = components["schemas"]["QuestionStatus"];
type QuestionType = components["schemas"]["QuestionType"];
type Difficulty = components["schemas"]["Difficulty"];
type Grade = components["schemas"]["Grade"];

const STATUSES: QuestionStatus[] = ["Draft", "Submitted", "Approved", "Rejected", "Archived"];
const TYPES: QuestionType[] = ["MultipleChoice", "TrueFalse", "ShortAnswer", "Essay"];
const DIFFICULTIES: Difficulty[] = ["Recognition", "Understanding", "Application", "HighApplication"];
const GRADES: Grade[] = [10, 11, 12];

export default function QuestionsPage() {
  const [page, setPage] = useState(1);
  const [status, setStatus] = useState<string>("");
  const [subjectId, setSubjectId] = useState<string>("");
  const [topicId, setTopicId] = useState<string>("");
  const [grade, setGrade] = useState<string>("");
  const [type, setType] = useState<string>("");
  const [difficulty, setDifficulty] = useState<string>("");
  const [mine, setMine] = useState(false);

  const subjectsQuery = useQuery({
    queryKey: ["catalog", "subjects"],
    queryFn: async () => {
      const { data, error } = await api.GET("/subjects", { params: { query: { pageSize: 100 } } });
      if (error) throw new Error(getApiErrorMessage(error));
      return data.items;
    },
  });

  const topicsQuery = useQuery({
    queryKey: ["catalog", "topics", subjectId, grade],
    queryFn: async () => {
      const { data, error } = await api.GET("/topics", {
        params: {
          query: {
            pageSize: 100,
            subjectId: subjectId || undefined,
            grade: grade ? (Number(grade) as Grade) : undefined,
          },
        },
      });
      if (error) throw new Error(getApiErrorMessage(error));
      return data.items;
    },
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
      return data;
    },
  });

  return (
    <section className="space-y-6">
      <div className="flex flex-wrap items-center justify-between gap-4">
        <h1 className="text-2xl font-bold text-slate-900">{vi.questions.title}</h1>
        <Link
          href="/questions/detail/"
          className="rounded bg-slate-900 px-4 py-2 text-sm font-medium text-white hover:bg-slate-800"
        >
          {vi.questions.createButton}
        </Link>
      </div>

      <div className="grid grid-cols-1 gap-3 rounded border border-slate-200 bg-slate-50 p-4 sm:grid-cols-3 lg:grid-cols-6">
        <select
          aria-label={vi.common.status}
          value={status}
          onChange={(e) => {
            setStatus(e.target.value);
            setPage(1);
          }}
          className="rounded border border-slate-300 bg-white px-2 py-1.5 text-sm"
        >
          <option value="">{vi.common.status}: {vi.common.all}</option>
          {STATUSES.map((s) => (
            <option key={s} value={s}>{vi.questions.statuses[s]}</option>
          ))}
        </select>

        <select
          aria-label={vi.questions.subject}
          value={subjectId}
          onChange={(e) => {
            setSubjectId(e.target.value);
            setTopicId("");
            setPage(1);
          }}
          className="rounded border border-slate-300 bg-white px-2 py-1.5 text-sm"
        >
          <option value="">{vi.questions.subject}: {vi.common.all}</option>
          {subjectsQuery.data?.map((s) => (
            <option key={s.id} value={s.id}>{s.name}</option>
          ))}
        </select>

        <select
          aria-label={vi.questions.topic}
          value={topicId}
          onChange={(e) => {
            setTopicId(e.target.value);
            setPage(1);
          }}
          className="rounded border border-slate-300 bg-white px-2 py-1.5 text-sm"
        >
          <option value="">{vi.questions.topic}: {vi.common.all}</option>
          {topicsQuery.data?.map((t) => (
            <option key={t.id} value={t.id}>{t.name}</option>
          ))}
        </select>

        <select
          aria-label={vi.questions.grade}
          value={grade}
          onChange={(e) => {
            setGrade(e.target.value);
            setTopicId("");
            setPage(1);
          }}
          className="rounded border border-slate-300 bg-white px-2 py-1.5 text-sm"
        >
          <option value="">{vi.questions.grade}: {vi.common.all}</option>
          {GRADES.map((g) => (
            <option key={g} value={g}>{vi.common.gradePrefix} {g}</option>
          ))}
        </select>

        <select
          aria-label={vi.questions.type}
          value={type}
          onChange={(e) => {
            setType(e.target.value);
            setPage(1);
          }}
          className="rounded border border-slate-300 bg-white px-2 py-1.5 text-sm"
        >
          <option value="">{vi.questions.type}: {vi.common.all}</option>
          {TYPES.map((t) => (
            <option key={t} value={t}>{vi.questions.types[t]}</option>
          ))}
        </select>

        <select
          aria-label={vi.questions.difficulty}
          value={difficulty}
          onChange={(e) => {
            setDifficulty(e.target.value);
            setPage(1);
          }}
          className="rounded border border-slate-300 bg-white px-2 py-1.5 text-sm"
        >
          <option value="">{vi.questions.difficulty}: {vi.common.all}</option>
          {DIFFICULTIES.map((d) => (
            <option key={d} value={d}>{vi.questions.difficulties[d]}</option>
          ))}
        </select>

        <label className="flex items-center gap-2 text-sm text-slate-700 sm:col-span-3 lg:col-span-6">
          <input
            type="checkbox"
            checked={mine}
            onChange={(e) => {
              setMine(e.target.checked);
              setPage(1);
            }}
          />
          {vi.questions.mineOnly}
        </label>
      </div>

      {questionsQuery.isLoading && <p className="text-sm text-slate-600">{vi.common.loading}</p>}
      {questionsQuery.isError && <p className="text-sm text-rose-600">{questionsQuery.error.message}</p>}

      {questionsQuery.data && questionsQuery.data.items.length === 0 && (
        <p className="rounded border border-slate-200 p-6 text-center text-sm text-slate-500">{vi.common.empty}</p>
      )}

      {questionsQuery.data && questionsQuery.data.items.length > 0 && (
        <div className="space-y-3">
          {questionsQuery.data.items.map((item) => (
            <article key={item.id} className="rounded border border-slate-200 bg-white p-4">
              <div className="flex flex-wrap items-center justify-between gap-2">
                <div className="flex flex-wrap items-center gap-2 text-xs">
                  <span className="rounded bg-slate-100 px-2 py-0.5 font-medium text-slate-800">
                    {vi.questions.statuses[item.status]}
                  </span>
                  <span className="rounded bg-blue-50 px-2 py-0.5 text-blue-800">
                    {vi.questions.types[item.type]}
                  </span>
                  <span className="rounded bg-slate-100 px-2 py-0.5 text-slate-700">
                    {vi.common.gradePrefix} {item.grade} · {vi.questions.difficulties[item.difficulty]}
                  </span>
                  {item.source === "AI" && (
                    <span className="rounded bg-amber-50 px-2 py-0.5 text-amber-800">
                      {vi.questions.aiFlags[item.aiVerification]}
                    </span>
                  )}
                </div>
                <Link
                  href={`/questions/detail/?id=${item.id}`}
                  className="text-sm font-medium text-slate-900 underline hover:text-slate-700"
                >
                  {vi.common.edit}
                </Link>
              </div>
              <div className="mt-2 line-clamp-3 text-sm text-slate-800">
                <MathMarkdown>{item.stem}</MathMarkdown>
              </div>
            </article>
          ))}

          <div className="flex items-center justify-between pt-2 text-sm">
            <button
              type="button"
              disabled={page <= 1}
              onClick={() => setPage((p) => Math.max(1, p - 1))}
              className="rounded border border-slate-300 px-3 py-1 disabled:opacity-40"
            >
              {vi.common.previousPage}
            </button>
            <span className="text-slate-600">
              {vi.common.pageLabel} {questionsQuery.data.page} / {Math.max(1, questionsQuery.data.totalPages)}
            </span>
            <button
              type="button"
              disabled={page >= questionsQuery.data.totalPages}
              onClick={() => setPage((p) => p + 1)}
              className="rounded border border-slate-300 px-3 py-1 disabled:opacity-40"
            >
              {vi.common.nextPage}
            </button>
          </div>
        </div>
      )}
    </section>
  );
}
