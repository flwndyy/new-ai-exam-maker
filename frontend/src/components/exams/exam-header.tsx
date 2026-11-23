"use client";

import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";

import { api } from "@/lib/api/client";
import { getApiErrorMessage } from "@/lib/api/error";
import type { components } from "@/lib/api/schema";
import { vi } from "@/messages/vi";

type ExamDetail = components["schemas"]["ExamDetail"];

/** Exam title (editable by the author), fixed subject/grade and the version selector. */
export function ExamHeader({
  exam,
  subjectName,
  selectedVid,
  onSelectVersion,
  canEditTitle,
}: {
  exam: ExamDetail;
  subjectName?: string;
  selectedVid: string;
  onSelectVersion: (vid: string) => void;
  canEditTitle: boolean;
}) {
  const queryClient = useQueryClient();
  const [editing, setEditing] = useState(false);
  const [title, setTitle] = useState(exam.title);
  const [error, setError] = useState<string | null>(null);

  const mutation = useMutation({
    mutationFn: async (next: string) => {
      const { data, error: apiError } = await api.PUT("/exams/{id}", { params: { path: { id: exam.id } }, body: { title: next } });
      if (apiError || !data) throw apiError ?? new Error(vi.common.requestFailed);
      return data;
    },
    onSuccess: (data) => {
      queryClient.setQueryData(["exam", exam.id], data);
      void queryClient.invalidateQueries({ queryKey: ["exams"] });
      setEditing(false);
      setError(null);
    },
    onError: (e: unknown) => setError(getApiErrorMessage(e)),
  });

  const selected = exam.versions.find((v) => v.id === selectedVid);
  const trimmed = title.trim();

  return (
    <header className="space-y-3">
      {editing ? (
        <form
          className="flex flex-wrap items-center gap-2"
          onSubmit={(e) => {
            e.preventDefault();
            if (trimmed && trimmed.length <= 300) mutation.mutate(trimmed);
          }}
        >
          <input
            aria-label={vi.exams.titleLabel}
            value={title}
            maxLength={300}
            onChange={(e) => setTitle(e.target.value)}
            className="min-w-0 flex-1 rounded border border-slate-300 px-2 py-1.5 text-lg"
          />
          <button
            type="submit"
            disabled={!trimmed || mutation.isPending}
            className="rounded bg-slate-900 px-3 py-1.5 text-sm text-white disabled:opacity-50"
          >
            {vi.exams.saveTitle}
          </button>
          <button
            type="button"
            onClick={() => {
              setEditing(false);
              setTitle(exam.title);
            }}
            className="rounded border px-3 py-1.5 text-sm"
          >
            {vi.exams.cancel}
          </button>
        </form>
      ) : (
        <div className="flex flex-wrap items-center gap-3">
          <h1 className="text-2xl font-bold text-slate-900">{exam.title}</h1>
          {canEditTitle && (
            <button type="button" onClick={() => setEditing(true)} className="text-sm text-slate-600 underline">
              {vi.exams.editTitle}
            </button>
          )}
        </div>
      )}
      {error && <p className="text-sm text-rose-600">{error}</p>}

      <p className="text-sm text-slate-600">
        {[subjectName, `${vi.exams.grade} ${exam.grade}`, `${vi.exams.author}: ${exam.createdBy.fullName}`].filter(Boolean).join(" · ")}
      </p>

      <label className="flex flex-wrap items-center gap-2 text-sm">
        <span className="font-medium text-slate-700">{vi.exams.versionLabel}</span>
        <select
          value={selectedVid}
          onChange={(e) => onSelectVersion(e.target.value)}
          className="rounded border border-slate-300 bg-white px-2 py-1.5"
        >
          {exam.versions.map((v) => (
            <option key={v.id} value={v.id}>
              {vi.exams.versionOption(v.versionNo, vi.review.examStatuses[v.status])}
            </option>
          ))}
        </select>
      </label>
      {selected?.status === "Rejected" && selected.reviewNote && (
        <p className="rounded border border-rose-200 bg-rose-50 p-2 text-sm text-rose-800">
          {vi.exams.rejectedNote}: {selected.reviewNote}
        </p>
      )}
    </header>
  );
}
