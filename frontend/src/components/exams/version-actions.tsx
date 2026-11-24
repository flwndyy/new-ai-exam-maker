"use client";

import { useMutation, useQueryClient } from "@tanstack/react-query";
import Link from "next/link";
import { useState } from "react";

import { api } from "@/lib/api/client";
import { getApiErrorCode, getApiErrorMessage } from "@/lib/api/error";
import type { components } from "@/lib/api/schema";
import { type Role, versionActions } from "@/lib/exams/rules";
import { vi } from "@/messages/vi";

type ExamDetail = components["schemas"]["ExamDetail"];
type ExamVersion = components["schemas"]["ExamVersion"];

type Action = "submit" | "withdraw" | "archive" | "createVersion";
type Failure = { status: number; error: unknown };

const TRANSITION_PATH = {
  submit: "/exam-versions/{vid}/submit",
  withdraw: "/exam-versions/{vid}/withdraw",
  archive: "/exam-versions/{vid}/archive",
} as const;

/** Author workflow buttons for the selected version (submit, withdraw, archive, new version). */
export function VersionActions({
  exam,
  version,
  role,
  meId,
  dirty,
  onNotApproved,
  onVersionCreated,
}: {
  exam: ExamDetail;
  version: ExamVersion;
  role: Role;
  meId: string;
  dirty: boolean;
  onNotApproved: (ids: string[]) => void;
  onVersionCreated: (vid: string) => void;
}) {
  const queryClient = useQueryClient();
  const [message, setMessage] = useState<{ kind: "error" | "success"; text: string; draftVid?: string } | null>(null);

  const draft = exam.versions.find((v) => v.status === "Draft");
  const flags = versionActions({ status: version.status, isAuthor: exam.createdBy.id === meId, role, hasDraft: Boolean(draft) });

  const mutation = useMutation({
    mutationFn: async (action: Action) => {
      if (action === "createVersion") {
        const { data, error, response } = await api.POST("/exams/{id}/versions", {
          params: { path: { id: exam.id } },
          body: { fromVersionId: version.id },
        });
        if (error || !data) throw { status: response.status, error } satisfies Failure;
        return { action, data };
      }
      const { data, error, response } = await api.POST(TRANSITION_PATH[action], { params: { path: { vid: version.id } } });
      if (error || !data) throw { status: response.status, error } satisfies Failure;
      return { action, data };
    },
    onSuccess: ({ action, data }) => {
      queryClient.setQueryData(["examVersion", data.id], data);
      if (action === "createVersion") {
        // The page resolves `vid` against the cached version list; add the new version first so the
        // URL switch is not overridden back to an older version before the refetch lands.
        queryClient.setQueryData<ExamDetail>(["exam", exam.id], (old) =>
          old
            ? {
                ...old,
                latestVersionNo: Math.max(old.latestVersionNo, data.versionNo),
                versions: [data, ...old.versions.filter((v) => v.id !== data.id)],
              }
            : old,
        );
      }
      void queryClient.invalidateQueries({ queryKey: ["exam", exam.id] });
      void queryClient.invalidateQueries({ queryKey: ["exams"] });
      if (action === "createVersion") {
        onVersionCreated(data.id);
        return;
      }
      onNotApproved([]);
      setMessage({ kind: "success", text: vi.exams.actionSuccess });
    },
    onError: (failure: unknown, action) => {
      const { status, error } = (failure ?? {}) as Partial<Failure>;
      if (action === "submit" && getApiErrorCode(error) === "QuestionsNotApproved") {
        onNotApproved((error as { invalidQuestionIds?: string[] }).invalidQuestionIds ?? []);
        setMessage({ kind: "error", text: vi.exams.notApproved });
        return;
      }
      if (action === "createVersion" && status === 409) {
        setMessage({ kind: "error", text: vi.exams.draftExists, draftVid: draft?.id });
        return;
      }
      setMessage({ kind: "error", text: getApiErrorMessage(error) });
    },
  });

  const run = (action: Action, confirmText?: string) => {
    if (confirmText && !window.confirm(confirmText)) return;
    setMessage(null);
    mutation.mutate(action);
  };

  if (!flags.submit && !flags.withdraw && !flags.archive && !flags.createVersion) return null;

  const btn = "rounded px-4 py-2 text-sm font-medium disabled:opacity-40";
  const busy = mutation.isPending;

  return (
    <div className="space-y-2">
      <div className="flex flex-wrap gap-2">
        {flags.submit && (
          <button
            type="button"
            disabled={busy || dirty || version.questionCount === 0}
            onClick={() => run("submit", vi.exams.confirmSubmit)}
            className={`${btn} bg-emerald-700 text-white hover:bg-emerald-800`}
          >
            {vi.exams.submit}
          </button>
        )}
        {flags.withdraw && (
          <button type="button" disabled={busy} onClick={() => run("withdraw")} className={`${btn} border border-slate-300`}>
            {vi.exams.withdraw}
          </button>
        )}
        {flags.createVersion && (
          <button
            type="button"
            disabled={busy}
            onClick={() => run("createVersion", dirty ? vi.exams.confirmDiscard : undefined)}
            className={`${btn} border border-slate-300`}
          >
            {vi.exams.createVersion}
          </button>
        )}
        {flags.archive && (
          <button
            type="button"
            disabled={busy}
            onClick={() => run("archive", vi.exams.confirmArchive)}
            className={`${btn} border border-rose-300 text-rose-700`}
          >
            {vi.exams.archive}
          </button>
        )}
      </div>
      {flags.submit && dirty && <p className="text-xs text-slate-600">{vi.exams.saveBeforeSubmit}</p>}
      {message && (
        <p className={`text-sm ${message.kind === "error" ? "text-rose-700" : "text-emerald-700"}`}>
          {message.text}{" "}
          {message.draftVid && (
            <Link href={`/exams/view/?id=${exam.id}&vid=${message.draftVid}`} className="underline">
              {vi.exams.openDraft}
            </Link>
          )}
        </p>
      )}
    </div>
  );
}
