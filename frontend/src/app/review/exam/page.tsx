"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Suspense, useState } from "react";

import { MathMarkdown } from "@/components/math-markdown";
import { QuestionPayloadView } from "@/components/questions/question-payload-view";
import { api } from "@/lib/api/client";
import { getApiErrorMessage } from "@/lib/api/error";
import type { components } from "@/lib/api/schema";
import { useAuth } from "@/lib/auth/auth-context";
import { vi } from "@/messages/vi";

type ExamVersion = components["schemas"]["ExamVersion"];

/** Bind checklist and note state to one submission round via parent key (`id + submittedAt + status`). */
function ExamDecisionForm({
  version,
  isAdmin,
  isSelfAuthor,
  onSuccess,
  onError,
}: {
  version: ExamVersion;
  isAdmin: boolean;
  isSelfAuthor: boolean;
  onSuccess: (message: string) => void;
  onError: (message: string) => void;
}) {
  const queryClient = useQueryClient();
  const [checks, setChecks] = useState<boolean[]>(vi.review.checklistItems.map(() => false));
  const [note, setNote] = useState("");

  const actionMutation = useMutation({
    mutationFn: async (action: "approve" | "reject" | "publish" | "unpublish" | "archive") => {
      if (action === "approve") {
        const { error } = await api.POST("/exam-versions/{vid}/approve", {
          params: { path: { vid: version.id } },
          body: { submittedAt: version.submittedAt!, note: note.trim() || null },
        });
        if (error) throw new Error(getApiErrorMessage(error));
        return action;
      }
      if (action === "reject") {
        const { error } = await api.POST("/exam-versions/{vid}/reject", {
          params: { path: { vid: version.id } },
          body: { submittedAt: version.submittedAt!, note: note.trim() },
        });
        if (error) throw new Error(getApiErrorMessage(error));
        return action;
      }
      const pathMap = {
        publish: "/exam-versions/{vid}/publish",
        unpublish: "/exam-versions/{vid}/unpublish",
        archive: "/exam-versions/{vid}/archive",
      } as const;
      const { error } = await api.POST(pathMap[action], { params: { path: { vid: version.id } } });
      if (error) throw new Error(getApiErrorMessage(error));
      return action;
    },
    onSuccess: (action) => {
      const messageMap = {
        approve: vi.review.approvedSuccess,
        reject: vi.review.rejectedSuccess,
        publish: vi.review.publishedSuccess,
        unpublish: vi.review.unpublishedSuccess,
        archive: vi.review.archivedSuccess,
      } as const;
      onSuccess(messageMap[action]);
      void queryClient.invalidateQueries({ queryKey: ["review", "exams"] });
      void queryClient.invalidateQueries({ queryKey: ["exam-version", version.id] });
    },
    onError: (err: Error) => {
      onError(err.message);
      // Re-read the exam version and review queue after a stale-round Conflict or concurrent state transition.
      void queryClient.invalidateQueries({ queryKey: ["exam-version", version.id] });
      void queryClient.invalidateQueries({ queryKey: ["review", "exams"] });
    },
  });

  const allChecked = checks.every(Boolean);

  return (
    <div className="space-y-4 rounded border border-slate-200 bg-white p-5">
      {version.status === "Submitted" && isSelfAuthor && (
        <p className="rounded border border-amber-300 bg-amber-50 p-3 text-sm text-amber-900">
          {vi.review.selfReviewNotice}
        </p>
      )}
      {version.status === "Submitted" && !isSelfAuthor && (
        <>
          <h2 className="text-sm font-semibold text-slate-900">{vi.review.checklistTitle}</h2>
          <div className="space-y-2">
            {vi.review.checklistItems.map((label, idx) => (
              <label key={idx} className="flex items-center gap-2 text-sm text-slate-700">
                <input
                  type="checkbox"
                  checked={checks[idx]}
                  onChange={(e) =>
                    setChecks((prev) => prev.map((val, i) => (i === idx ? e.target.checked : val)))
                  }
                />
                {label}
              </label>
            ))}
          </div>

          <label className="block text-sm">
            <span className="font-medium text-slate-700">{vi.review.noteLabel}</span>
            <textarea
              rows={3}
              placeholder={vi.review.notePlaceholder}
              value={note}
              onChange={(e) => setNote(e.target.value)}
              className="mt-1 w-full rounded border border-slate-300 p-2.5 text-sm"
            />
          </label>
        </>
      )}

      <div className="flex flex-wrap gap-3">
        {version.status === "Submitted" && !isSelfAuthor && (
          <>
            <button
              type="button"
              disabled={actionMutation.isPending}
              onClick={() => {
                if (!allChecked) {
                  onError(vi.review.checklistRequired);
                  return;
                }
                actionMutation.mutate("approve");
              }}
              className="rounded bg-emerald-600 px-4 py-2 text-sm font-medium text-white hover:bg-emerald-700 disabled:opacity-50"
            >
              {vi.review.approve}
            </button>
            <button
              type="button"
              disabled={actionMutation.isPending}
              onClick={() => {
                if (!note.trim()) {
                  onError(vi.review.rejectNeedsNote);
                  return;
                }
                actionMutation.mutate("reject");
              }}
              className="rounded bg-rose-600 px-4 py-2 text-sm font-medium text-white hover:bg-rose-700 disabled:opacity-50"
            >
              {vi.review.reject}
            </button>
          </>
        )}

        {isAdmin && version.status === "Approved" && (
          <button
            type="button"
            disabled={actionMutation.isPending}
            onClick={() => actionMutation.mutate("publish")}
            className="rounded bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700"
          >
            {vi.review.publish}
          </button>
        )}

        {isAdmin && version.status === "Published" && (
          <button
            type="button"
            disabled={actionMutation.isPending}
            onClick={() => actionMutation.mutate("unpublish")}
            className="rounded border border-slate-300 px-4 py-2 text-sm hover:bg-slate-50"
          >
            {vi.review.unpublish}
          </button>
        )}

        {(version.status === "Approved" || version.status === "Rejected") && (
          <button
            type="button"
            disabled={actionMutation.isPending}
            onClick={() => actionMutation.mutate("archive")}
            className="rounded border border-slate-300 px-4 py-2 text-sm hover:bg-slate-50"
          >
            {vi.review.archive}
          </button>
        )}
      </div>
    </div>
  );
}

function ReviewExamInner() {
  const vid = useSearchParams().get("id");
  const { me } = useAuth();
  const [feedback, setFeedback] = useState<string | null>(null);
  const [errorMsg, setErrorMsg] = useState<string | null>(null);

  const canReview = me?.role === "head" || me?.role === "admin";

  const versionQuery = useQuery({
    queryKey: ["exam-version", vid],
    enabled: Boolean(vid) && canReview,
    queryFn: async () => {
      const { data, error } = await api.GET("/exam-versions/{vid}", { params: { path: { vid: vid! } } });
      if (error) throw new Error(getApiErrorMessage(error));
      return data;
    },
  });

  if (!canReview) {
    return <p className="rounded border border-rose-200 bg-rose-50 p-4 text-sm text-rose-800">{vi.common.forbidden}</p>;
  }
  if (!vid) return <p className="text-sm text-rose-600">{vi.review.missingId}</p>;

  const v = versionQuery.data;

  return (
    <section className="space-y-6">
      <div>
        <Link href="/review/" className="text-sm text-slate-600 hover:underline">
          ← {vi.common.back}
        </Link>
        <h1 className="mt-1 text-2xl font-bold text-slate-900">{vi.review.examTitle}</h1>
      </div>

      {versionQuery.isLoading && <p className="text-sm text-slate-600">{vi.common.loading}</p>}
      {versionQuery.isError && <p className="text-sm text-rose-600">{versionQuery.error.message}</p>}
      {feedback && <p className="rounded border border-emerald-300 bg-emerald-50 p-3 text-sm text-emerald-800">{feedback}</p>}
      {errorMsg && <p className="rounded border border-rose-300 bg-rose-50 p-3 text-sm text-rose-800">{errorMsg}</p>}

      {v && (
        <div className="space-y-5">
          <div className="rounded border border-slate-200 bg-white p-5">
            <div className="flex flex-wrap items-center justify-between gap-2">
              <h2 className="text-lg font-semibold text-slate-900">
                {v.examTitle} (v{v.versionNo})
              </h2>
              <span className="rounded bg-slate-100 px-2.5 py-1 text-xs font-medium text-slate-800">
                {vi.review.examStatuses[v.status]}
              </span>
            </div>
            <p className="mt-2 text-sm text-slate-600">
              {vi.common.gradePrefix} {v.grade} · {vi.review.examDuration}: {v.durationMinutes} · {vi.review.examQuestionCount}: {v.questionCount} · {vi.review.examTotalPoints}: {v.totalPoints}
            </p>
            {(v.warnings ?? []).length > 0 && (
              <div className="mt-3 rounded border border-amber-300 bg-amber-50 p-3 text-xs text-amber-900">
                <p className="font-semibold">{vi.review.warningsTitle}:</p>
                <ul className="mt-1 list-disc pl-4">
                  {(v.warnings ?? []).map((w, idx) => (
                    <li key={idx}>{w.message}</li>
                  ))}
                </ul>
              </div>
            )}
          </div>

          <div className="space-y-3">
            {v.items.map((item) => (
              <article key={item.sourceQuestionId} className="space-y-2 rounded border border-slate-200 bg-white p-4">
                <div className="flex items-center justify-between text-xs text-slate-500">
                  <span>
                    {vi.review.questionItemPrefix} {item.sortOrder}
                    {item.difficulty ? ` · ${vi.questions.difficulties[item.difficulty]}` : ""}
                  </span>
                  <span className="font-medium text-slate-700">{item.points} {vi.review.points}</span>
                </div>
                {item.stem && (
                  <div className="text-sm text-slate-900">
                    <MathMarkdown>{item.stem}</MathMarkdown>
                  </div>
                )}
                {item.type && item.payload && <QuestionPayloadView type={item.type} payload={item.payload} />}
                {item.explanation && (
                  <div className="rounded border border-slate-200 bg-slate-50 p-3 text-sm">
                    <p className="font-medium text-slate-700">{vi.questions.explanation}:</p>
                    <div className="mt-1">
                      <MathMarkdown>{item.explanation}</MathMarkdown>
                    </div>
                  </div>
                )}
              </article>
            ))}
          </div>

          <ExamDecisionForm
            key={`${v.id}-${v.submittedAt ?? ""}-${v.status}`}
            version={v}
            isAdmin={me?.role === "admin"}
            isSelfAuthor={me?.id === v.createdBy.id}
            onSuccess={(msg) => {
              setErrorMsg(null);
              setFeedback(msg);
            }}
            onError={(msg) => {
              setFeedback(null);
              setErrorMsg(msg);
            }}
          />
        </div>
      )}
    </section>
  );
}

export default function ReviewExamPage() {
  return (
    <Suspense fallback={<p>{vi.common.loading}</p>}>
      <ReviewExamInner />
    </Suspense>
  );
}
