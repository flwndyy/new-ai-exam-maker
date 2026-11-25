"use client";

import { useMutation, useQueryClient } from "@tanstack/react-query";
import Link from "next/link";
import { type ReactNode, useId, useState } from "react";
import { createPortal } from "react-dom";

import { Button } from "@/components/ui/button";
import { Notice } from "@/components/ui/notice";
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

/**
 * Author workflow buttons for the selected version (submit, withdraw, archive, new version).
 * `buttonsHost` places the buttons in the board header: undefined renders them inline, an element
 * renders them there through a portal, and null renders none until the host exists. Messages stay inline.
 */
export function VersionActions({
  exam,
  version,
  role,
  meId,
  dirty,
  onNotApproved,
  onVersionCreated,
  buttonsHost,
  beforeSubmit,
}: {
  exam: ExamDetail;
  version: ExamVersion;
  role: Role;
  meId: string;
  dirty: boolean;
  onNotApproved: (ids: string[]) => void;
  onVersionCreated: (vid: string) => void;
  buttonsHost?: HTMLElement | null;
  /** Rendered right before the submit button (the page puts the composer Save here). */
  beforeSubmit?: ReactNode;
}) {
  const queryClient = useQueryClient();
  const submitReasonId = useId();
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
                ...(data.versionNo >= old.latestVersionNo
                  ? { latestVersionNo: data.versionNo, latestVersionId: data.id, latestVersionStatus: data.status }
                  : {}),
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

  const busy = mutation.isPending;
  const onBoard = buttonsHost !== undefined;
  const submitReason = dirty ? vi.exams.saveBeforeSubmit : version.questionCount === 0 ? vi.exams.addBeforeSubmit : undefined;

  // DOM order is the visual and keyboard order: secondary actions, then `beforeSubmit` (the composer Save), then submit.
  const buttons = (
    <>
      {flags.withdraw && (
        <Button variant={onBoard ? "boardPrimary" : "secondary"} disabled={busy} onClick={() => run("withdraw")}>
          {vi.exams.withdraw}
        </Button>
      )}
      {flags.createVersion && (
        <Button
          variant={onBoard ? "boardSecondary" : "secondary"}
         
          disabled={busy}
          onClick={() => run("createVersion", dirty ? vi.exams.confirmDiscard : undefined)}
        >
          {vi.exams.createVersion}
        </Button>
      )}
      {flags.archive && (
        <Button
          variant={onBoard ? "boardSecondary" : "danger"}
         
          disabled={busy}
          onClick={() => run("archive", vi.exams.confirmArchive)}
        >
          {vi.exams.archive}
        </Button>
      )}
      {flags.submit && beforeSubmit}
      {flags.submit && (
        <Button
          variant={onBoard ? "boardPrimary" : "approve"}
          disabled={busy || dirty || version.questionCount === 0}
          aria-describedby={submitReason ? submitReasonId : undefined}
          onClick={() => run("submit", vi.exams.confirmSubmit)}
        >
          {vi.exams.submit}
        </Button>
      )}
    </>
  );

  return (
    <>
      {buttonsHost ? createPortal(buttons, buttonsHost) : onBoard ? null : <div className="mb-3.5 flex flex-wrap gap-2">{buttons}</div>}
      {flags.submit && submitReason && (
        <p id={submitReasonId} className="mb-3.5 text-[12.5px] text-ink-soft">
          {submitReason}
        </p>
      )}
      {message && (
        <Notice tone={message.kind === "error" ? "error" : "success"} className="mb-3.5">
          {message.text}{" "}
          {message.draftVid && (
            <Link href={`/exams/view/?id=${exam.id}&vid=${message.draftVid}`} className="underline">
              {vi.exams.openDraft}
            </Link>
          )}
        </Notice>
      )}
    </>
  );
}
