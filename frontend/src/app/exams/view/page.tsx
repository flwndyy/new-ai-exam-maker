"use client";

// Dynamic resources use a query string (/exams/view/?id=...&vid=...) instead of /exams/[id],
// because static export must know every route at build time (docs/context.md §9.2).

import { useQuery } from "@tanstack/react-query";
import { useRouter, useSearchParams } from "next/navigation";
import { type ReactNode, Suspense, useEffect, useId, useState } from "react";

import { ExamComposer } from "@/components/exams/exam-composer";
import { ExportLinks } from "@/components/exams/export-links";
import { ExamHeader } from "@/components/exams/exam-header";
import { VersionActions } from "@/components/exams/version-actions";
import { VersionReadonly } from "@/components/exams/version-readonly";
import { PageHeader } from "@/components/shell/page-header";
import { Button } from "@/components/ui/button";
import { EmptyState } from "@/components/ui/empty-state";
import { Notice } from "@/components/ui/notice";
import { fetchAllSubjects } from "@/lib/api/catalog";
import { api } from "@/lib/api/client";
import { getUiErrorMessage } from "@/lib/api/error";
import { useAuth } from "@/lib/auth/auth-context";
import { canExport, pickDefaultVersionId, type Role, versionActions } from "@/lib/exams/rules";
import { vi } from "@/messages/vi";

type HttpFailure = { status: number; error: unknown };

const EMPTY_IDS: ReadonlySet<string> = new Set();

/** Board header plus content wrapper for states that have no exam yet (loading, error, missing id). */
function Frame({ children }: { children: ReactNode }) {
  return (
    <>
      <PageHeader title={vi.exams.listTitle} />
      <div className="px-4 py-5 sm:px-7 sm:py-6">{children}</div>
    </>
  );
}

function ExamView({ id }: { id: string }) {
  const { me } = useAuth();
  const router = useRouter();
  const requestedVid = useSearchParams().get("vid");
  const [dirty, setDirty] = useState(false);
  const [highlight, setHighlight] = useState<{ stamp: string; ids: ReadonlySet<string> }>({ stamp: "", ids: new Set() });
  // Board action row: the version buttons and the composer's Save render into it through portals.
  const [actionsHost, setActionsHost] = useState<HTMLElement | null>(null);
  // The composer form is submitted by the Save button in the board header, so the page owns the form id.
  const formId = useId();
  const [saving, setSaving] = useState(false);

  const examQuery = useQuery({
    queryKey: ["exam", id],
    retry: false,
    queryFn: async () => {
      const { data, error, response } = await api.GET("/exams/{id}", { params: { path: { id } } });
      if (error || !data) throw { status: response.status, error } satisfies HttpFailure;
      return data;
    },
  });
  const exam = examQuery.data;
  const vid = exam ? pickDefaultVersionId(exam.versions, requestedVid) : undefined;

  // Pin the resolved version in the URL: otherwise a later status change (submit, review) would make
  // pickDefaultVersionId choose another version and silently swap the page under the user.
  useEffect(() => {
    if (vid && vid !== requestedVid) router.replace(`/exams/view/?id=${id}&vid=${vid}`);
  }, [id, vid, requestedVid, router]);

  const versionQuery = useQuery({
    queryKey: ["examVersion", vid],
    enabled: Boolean(vid),
    retry: false,
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
    queryFn: async () => {
      const { data, error, response } = await api.GET("/exam-versions/{vid}", { params: { path: { vid: vid! } } });
      if (error || !data) throw { status: response.status, error } satisfies HttpFailure;
      return data;
    },
  });

  // "Not approved" highlights belong to one server copy; a save, transition or version switch drops them.
  const versionStamp = versionQuery.data ? `${versionQuery.data.id}:${versionQuery.data.updatedAt}` : "";
  const highlightIds = highlight.stamp === versionStamp ? highlight.ids : EMPTY_IDS;

  const subjectsQuery = useQuery({ queryKey: ["catalog", "subjects", "all"], queryFn: () => fetchAllSubjects(), enabled: Boolean(exam) });

  if (examQuery.isLoading) {
    return (
      <Frame>
        <EmptyState busy title={vi.common.loading} />
      </Frame>
    );
  }
  if (examQuery.isError || !exam) {
    const status = (examQuery.error as HttpFailure | null)?.status;
    return (
      <Frame>
        <Notice tone="error">{status === 404 || status === 403 ? vi.exams.notFound : getUiErrorMessage(examQuery.error)}</Notice>
      </Frame>
    );
  }

  const selectVersion = (next: string) => {
    if (next === vid) return;
    if (dirty && !window.confirm(vi.exams.confirmDiscard)) return;
    setDirty(false);
    router.replace(`/exams/view/?id=${id}&vid=${next}`);
  };

  const version = versionQuery.data;
  const summary = exam.versions.find((v) => v.id === vid);
  const isAuthor = me?.id === exam.createdBy.id;
  const role = (me?.role ?? "teacher") as Role;
  const actions = versionActions({
    status: version?.status ?? summary?.status ?? "Archived",
    isAuthor,
    role,
    hasDraft: exam.versions.some((v) => v.status === "Draft"),
  });
  const versionStatus = (versionQuery.error as HttpFailure | null)?.status;
  const selected = exam.versions.find((v) => v.id === vid);

  return (
    <>
      <ExamHeader
        key={exam.title}
        exam={exam}
        subjectName={subjectsQuery.data?.find((s) => s.id === exam.subjectId)?.name}
        selectedVid={vid ?? ""}
        onSelectVersion={selectVersion}
        canEditTitle={isAuthor && role !== "admin"}
        dirty={dirty}
        actions={<div ref={setActionsHost} className="flex flex-wrap gap-2" />}
      />

      <div className="px-4 py-5 sm:px-7 sm:py-6">
        {selected?.status === "Rejected" && selected.reviewNote && (
          <Notice tone="error" title={vi.exams.rejectedNote} className="mb-4">
            {selected.reviewNote}
          </Notice>
        )}

        {version && me && (
          <VersionActions
            key={`actions-${version.id}`}
            exam={exam}
            version={version}
            role={role}
            meId={me.id}
            dirty={dirty}
            buttonsHost={actionsHost}
            beforeSubmit={
              actions.canEdit ? (
                <Button type="submit" form={formId} variant="boardPrimary" disabled={!dirty || saving}>
                  {saving ? vi.common.saving : vi.exams.save}
                </Button>
              ) : null
            }
            onNotApproved={(ids) => setHighlight({ stamp: versionStamp, ids: new Set(ids) })}
            onVersionCreated={(next) => {
              setDirty(false);
              router.replace(`/exams/view/?id=${id}&vid=${next}`);
            }}
          />
        )}

        {version?.warnings && version.warnings.length > 0 && (
          <Notice tone="warn" title={vi.exams.warningsTitle} className="mb-4">
            <ul className="list-disc pl-5">
              {version.warnings.map((w, i) => (
                <li key={`${w.code}-${w.sourceQuestionId ?? i}`}>{w.message}</li>
              ))}
            </ul>
          </Notice>
        )}

        {versionQuery.isLoading && <EmptyState busy title={vi.common.loading} />}
        {versionQuery.isError && (
          <Notice tone="error" className="mb-4">
            {versionStatus === 403 ? vi.exams.studentSheetOnly : versionStatus === 404 ? vi.exams.notFound : getUiErrorMessage(versionQuery.error)}
          </Notice>
        )}
        {/* A 403 on the detail means "no answer access": the student sheet may still be exportable. */}
        {versionStatus === 403 && vid && summary && canExport(summary.status) && (
          <div className="mb-4 flex flex-wrap gap-2">
            <ExportLinks examId={id} versionId={vid} canReadAnswers={false} />
          </div>
        )}
        {version &&
          (actions.canEdit && me ? (
            <ExamComposer
              key={`composer-${version.id}`}
              exam={exam}
              version={version}
              meId={me.id}
              highlightIds={highlightIds}
              onDirtyChange={setDirty}
              externalForm={{ formId, onSavingChange: setSaving }}
            />
          ) : (
            <VersionReadonly
              version={version}
              actions={canExport(version.status) ? <ExportLinks examId={id} versionId={version.id} canReadAnswers /> : undefined}
            />
          ))}
      </div>
    </>
  );
}

function ExamViewRoute() {
  const id = useSearchParams().get("id");
  if (!id) {
    return (
      <Frame>
        <Notice tone="error">{vi.exams.missingId}</Notice>
      </Frame>
    );
  }
  return <ExamView id={id} />;
}

export default function ExamViewPage() {
  return (
    <Suspense
      fallback={
        <Frame>
          <EmptyState busy title={vi.common.loading} />
        </Frame>
      }
    >
      <ExamViewRoute />
    </Suspense>
  );
}
