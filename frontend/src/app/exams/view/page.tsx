"use client";

// Dynamic resources use a query string (/exams/view/?id=...&vid=...) instead of /exams/[id],
// because static export must know every route at build time (docs/context.md §9.2).

import { useQuery } from "@tanstack/react-query";
import { useRouter, useSearchParams } from "next/navigation";
import { Suspense, useEffect, useState } from "react";

import { ExamComposer } from "@/components/exams/exam-composer";
import { ExamHeader } from "@/components/exams/exam-header";
import { VersionActions } from "@/components/exams/version-actions";
import { VersionReadonly } from "@/components/exams/version-readonly";
import { fetchAllSubjects } from "@/lib/api/catalog";
import { api } from "@/lib/api/client";
import { getUiErrorMessage } from "@/lib/api/error";
import { useAuth } from "@/lib/auth/auth-context";
import { pickDefaultVersionId, type Role, versionActions } from "@/lib/exams/rules";
import { vi } from "@/messages/vi";

type HttpFailure = { status: number; error: unknown };

const EMPTY_IDS: ReadonlySet<string> = new Set();

function ExamView({ id }: { id: string }) {
  const { me } = useAuth();
  const router = useRouter();
  const requestedVid = useSearchParams().get("vid");
  const [dirty, setDirty] = useState(false);
  const [highlight, setHighlight] = useState<{ stamp: string; ids: ReadonlySet<string> }>({ stamp: "", ids: new Set() });

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

  if (examQuery.isLoading) return <p>{vi.common.loading}</p>;
  if (examQuery.isError || !exam) {
    const status = (examQuery.error as HttpFailure | null)?.status;
    return <p className="text-rose-600">{status === 404 || status === 403 ? vi.exams.notFound : getUiErrorMessage(examQuery.error)}</p>;
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

  return (
    <section className="space-y-6">
      <ExamHeader
        key={exam.title}
        exam={exam}
        subjectName={subjectsQuery.data?.find((s) => s.id === exam.subjectId)?.name}
        selectedVid={vid ?? ""}
        onSelectVersion={selectVersion}
        canEditTitle={isAuthor && role !== "admin"}
      />

      {version && me && (
        <VersionActions
          key={version.id}
          exam={exam}
          version={version}
          role={role}
          meId={me.id}
          dirty={dirty}
          onNotApproved={(ids) => setHighlight({ stamp: versionStamp, ids: new Set(ids) })}
          onVersionCreated={(next) => {
            setDirty(false);
            router.replace(`/exams/view/?id=${id}&vid=${next}`);
          }}
        />
      )}

      {version?.warnings && version.warnings.length > 0 && (
        <div className="rounded border border-amber-300 bg-amber-50 p-3 text-sm text-amber-900">
          <p className="font-medium">{vi.exams.warningsTitle}</p>
          <ul className="list-disc pl-5">
            {version.warnings.map((w, i) => (
              <li key={`${w.code}-${w.sourceQuestionId ?? i}`}>{w.message}</li>
            ))}
          </ul>
        </div>
      )}

      {versionQuery.isLoading && <p>{vi.common.loading}</p>}
      {versionQuery.isError && (
        <p className="text-rose-600">
          {versionStatus === 403 ? vi.exams.studentSheetOnly : versionStatus === 404 ? vi.exams.notFound : getUiErrorMessage(versionQuery.error)}
        </p>
      )}
      {version &&
        (actions.canEdit && me ? (
          <ExamComposer key={version.id} exam={exam} version={version} meId={me.id} highlightIds={highlightIds} onDirtyChange={setDirty} />
        ) : (
          <VersionReadonly version={version} />
        ))}
    </section>
  );
}

function ExamViewRoute() {
  const id = useSearchParams().get("id");
  if (!id) return <p className="text-red-600">{vi.exams.missingId}</p>;
  return <ExamView id={id} />;
}

export default function ExamViewPage() {
  return (
    <Suspense fallback={<p>{vi.common.loading}</p>}>
      <ExamViewRoute />
    </Suspense>
  );
}
