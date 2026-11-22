"use client";

import { useQuery } from "@tanstack/react-query";
import Link from "next/link";
import { useState } from "react";

import { MathMarkdown } from "@/components/math-markdown";
import { api } from "@/lib/api/client";
import { getApiErrorMessage } from "@/lib/api/error";
import { useAuth } from "@/lib/auth/auth-context";
import { vi } from "@/messages/vi";

export default function ReviewQueuePage() {
  const { me } = useAuth();
  const [tab, setTab] = useState<"questions" | "exams">("questions");
  const [page, setPage] = useState(1);
  const [subjectId, setSubjectId] = useState("");
  const [topicId, setTopicId] = useState("");

  const canReview = me?.role === "head" || me?.role === "admin";

  const subjectsQuery = useQuery({
    queryKey: ["catalog", "subjects"],
    enabled: canReview,
    queryFn: async () => {
      const { data, error } = await api.GET("/subjects", { params: { query: { pageSize: 100 } } });
      if (error) throw new Error(getApiErrorMessage(error));
      return data.items;
    },
  });

  const topicsQuery = useQuery({
    queryKey: ["catalog", "topics", subjectId],
    enabled: canReview && tab === "questions" && Boolean(subjectId),
    queryFn: async () => {
      const { data, error } = await api.GET("/topics", {
        params: { query: { pageSize: 100, subjectId } },
      });
      if (error) throw new Error(getApiErrorMessage(error));
      return data.items;
    },
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
      return data;
    },
  });

  if (!canReview) {
    return <p className="rounded border border-rose-200 bg-rose-50 p-4 text-sm text-rose-800">{vi.common.forbidden}</p>;
  }

  const activeData = tab === "questions" ? questionsQuery.data : examsQuery.data;
  const isLoading = tab === "questions" ? questionsQuery.isLoading : examsQuery.isLoading;
  const error = tab === "questions" ? questionsQuery.error : examsQuery.error;

  return (
    <section className="space-y-6">
      <h1 className="text-2xl font-bold text-slate-900">{vi.review.title}</h1>

      <div className="flex flex-wrap gap-2 border-b border-slate-200 pb-2">
        <button
          type="button"
          onClick={() => {
            setTab("questions");
            setPage(1);
          }}
          className={`rounded px-3 py-1.5 text-sm font-medium ${
            tab === "questions" ? "bg-slate-900 text-white" : "text-slate-600 hover:bg-slate-100"
          }`}
        >
          {vi.review.questionsTab}
        </button>
        <button
          type="button"
          onClick={() => {
            setTab("exams");
            setTopicId("");
            setPage(1);
          }}
          className={`rounded px-3 py-1.5 text-sm font-medium ${
            tab === "exams" ? "bg-slate-900 text-white" : "text-slate-600 hover:bg-slate-100"
          }`}
        >
          {vi.review.examsTab}
        </button>
      </div>

      <div className="flex flex-wrap gap-3">
        <select
          aria-label={vi.questions.subject}
          value={subjectId}
          onChange={(e) => {
            setSubjectId(e.target.value);
            setTopicId("");
            setPage(1);
          }}
          className="rounded border border-slate-300 bg-white px-2.5 py-1.5 text-sm"
        >
          <option value="">{vi.questions.subject}: {vi.common.all}</option>
          {subjectsQuery.data?.map((s) => (
            <option key={s.id} value={s.id}>{s.name}</option>
          ))}
        </select>

        {tab === "questions" && (
          <select
            aria-label={vi.questions.topic}
            disabled={!subjectId}
            value={topicId}
            onChange={(e) => {
              setTopicId(e.target.value);
              setPage(1);
            }}
            className="rounded border border-slate-300 bg-white px-2.5 py-1.5 text-sm disabled:bg-slate-100"
          >
            <option value="">{vi.questions.topic}: {vi.common.all}</option>
            {topicsQuery.data?.map((t) => (
              <option key={t.id} value={t.id}>{t.name}</option>
            ))}
          </select>
        )}
      </div>

      {isLoading && <p className="text-sm text-slate-600">{vi.common.loading}</p>}
      {error && <p className="text-sm text-rose-600">{error.message}</p>}

      {activeData && activeData.items.length === 0 && (
        <p className="rounded border border-slate-200 p-6 text-center text-sm text-slate-500">{vi.common.empty}</p>
      )}

      {tab === "questions" && questionsQuery.data && questionsQuery.data.items.length > 0 && (
        <div className="space-y-3">
          {questionsQuery.data.items.map((item) => (
            <article key={item.id} className="rounded border border-slate-200 bg-white p-4">
              <div className="flex flex-wrap items-center justify-between gap-2">
                <div className="flex flex-wrap items-center gap-2 text-xs">
                  <span className="rounded bg-blue-50 px-2 py-0.5 text-blue-800">{vi.questions.types[item.type]}</span>
                  <span className="rounded bg-slate-100 px-2 py-0.5 text-slate-700">
                    {vi.common.gradePrefix} {item.grade} · {item.createdBy.fullName}
                  </span>
                  {item.source === "AI" && (
                    <span className="rounded bg-amber-50 px-2 py-0.5 text-amber-800">
                      {vi.questions.aiFlags[item.aiVerification]}
                    </span>
                  )}
                </div>
                <Link
                  href={`/review/question/?id=${item.id}`}
                  className="rounded bg-slate-900 px-3 py-1 text-xs font-medium text-white hover:bg-slate-800"
                >
                  {vi.review.openReview}
                </Link>
              </div>
              <div className="mt-2 line-clamp-3 text-sm text-slate-800">
                <MathMarkdown>{item.stem}</MathMarkdown>
              </div>
            </article>
          ))}
        </div>
      )}

      {tab === "exams" && examsQuery.data && examsQuery.data.items.length > 0 && (
        <div className="space-y-3">
          {examsQuery.data.items.map((item) => (
            <article key={item.id} className="flex flex-wrap items-center justify-between gap-3 rounded border border-slate-200 bg-white p-4">
              <div>
                <h2 className="font-semibold text-slate-900">
                  {item.examTitle} (v{item.versionNo})
                </h2>
                <p className="mt-1 text-xs text-slate-600">
                  {vi.common.gradePrefix} {item.grade} · {item.questionCount} {vi.review.questionCountSuffix} · {item.totalPoints} {vi.review.points} · {item.createdBy.fullName}
                </p>
              </div>
              <Link
                href={`/review/exam/?id=${item.id}`}
                className="rounded bg-slate-900 px-3 py-1.5 text-xs font-medium text-white hover:bg-slate-800"
              >
                {vi.review.openReview}
              </Link>
            </article>
          ))}
        </div>
      )}

      {activeData && (activeData.items.length > 0 || page > 1) && (
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
            {vi.common.pageLabel} {activeData.page} / {Math.max(1, activeData.totalPages)}
          </span>
          <button
            type="button"
            disabled={page >= activeData.totalPages}
            onClick={() => setPage((p) => p + 1)}
            className="rounded border border-slate-300 px-3 py-1 disabled:opacity-40"
          >
            {vi.common.nextPage}
          </button>
        </div>
      )}
    </section>
  );
}
