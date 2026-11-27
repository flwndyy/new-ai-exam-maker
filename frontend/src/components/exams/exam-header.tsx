"use client";

import { useMutation, useQueryClient } from "@tanstack/react-query";
import { type ReactNode, useId, useState } from "react";

import { PageHeader } from "@/components/shell/page-header";
import { Notice } from "@/components/ui/notice";
import { api } from "@/lib/api/client";
import { getApiErrorMessage } from "@/lib/api/error";
import type { components } from "@/lib/api/schema";
import { vi } from "@/messages/vi";

type ExamDetail = components["schemas"]["ExamDetail"];

/**
 * Board header of the composer: breadcrumb, title (editable by the author), fixed subject/grade, the
 * version selector, the unsaved-changes marker and the action buttons passed in by the page.
 */
export function ExamHeader({
  exam,
  subjectName,
  selectedVid,
  onSelectVersion,
  canEditTitle,
  dirty = false,
  actions,
}: {
  exam: ExamDetail;
  subjectName?: string;
  selectedVid: string;
  onSelectVersion: (vid: string) => void;
  canEditTitle: boolean;
  dirty?: boolean;
  actions?: ReactNode;
}) {
  const queryClient = useQueryClient();
  const [editing, setEditing] = useState(false);
  const [title, setTitle] = useState(exam.title);
  const [error, setError] = useState<string | null>(null);
  const versionId = useId();

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

  const trimmed = title.trim();
  const buttonBase =
    "rounded-md border px-3 py-1.5 text-[13px] font-semibold transition-colors duration-150 disabled:cursor-not-allowed disabled:opacity-45";

  // The edit control lives next to the heading, never inside the <h1>: its name stays just the title.
  const titleEditor = editing ? (
    <form
      className="mb-2 flex flex-wrap items-center gap-2 text-[14px]"
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
        className="min-w-0 flex-1 rounded-md border border-white/50 bg-white px-3 py-1.5 text-[15px] text-ink"
      />
      <button type="submit" disabled={!trimmed || mutation.isPending} className={`${buttonBase} border-transparent bg-white text-accent-strong hover:bg-on-board-muted`}>
        {vi.exams.saveTitle}
      </button>
      <button
        type="button"
        onClick={() => {
          setEditing(false);
          setTitle(exam.title);
        }}
        className={`${buttonBase} border-white/50 text-white hover:bg-white/15`}
      >
        {vi.exams.cancel}
      </button>
    </form>
  ) : null;

  const details = [subjectName, `${vi.exams.grade} ${exam.grade}`, `${vi.exams.author}: ${exam.createdBy.fullName}`].filter(Boolean).join(", ");

  return (
    <>
      <PageHeader
        crumb={{ href: "/exams/", label: vi.nav.exams }}
        title={exam.title}
        description={
          <>
            {titleEditor}
            <p>
              {details}
              {canEditTitle && !editing && (
                <button
                  type="button"
                  onClick={() => setEditing(true)}
                  className="ml-3 text-[13px] font-medium text-on-board-muted underline underline-offset-2 hover:text-on-board"
                >
                  {vi.exams.editTitle}
                </button>
              )}
            </p>
            <div className="mt-2 flex flex-wrap items-center gap-x-3 gap-y-1.5 text-[13px]">
              <label htmlFor={versionId} className="flex items-center gap-2">
                <span>{vi.exams.versionLabel}</span>
                <select
                  id={versionId}
                  value={selectedVid}
                  onChange={(e) => onSelectVersion(e.target.value)}
                  className="rounded-md bg-white px-2.5 py-1 text-[13px] text-ink"
                >
                  {exam.versions.map((v) => (
                    <option key={v.id} value={v.id}>
                      {vi.exams.versionOption(v.versionNo, vi.review.examStatuses[v.status])}
                    </option>
                  ))}
                </select>
              </label>
              {dirty && <span className="font-semibold text-white">{vi.exams.unsavedChanges}</span>}
            </div>
          </>
        }
        actions={actions}
      />
      {error && (
        <div className="px-4 pt-5 sm:px-7">
          <Notice tone="error">{error}</Notice>
        </div>
      )}
    </>
  );
}
