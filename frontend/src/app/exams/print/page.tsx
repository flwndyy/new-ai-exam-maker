"use client";

// Print preview for one exam version. Each mode calls ONLY its own endpoint: the student mode never requests
// the answer key, so answers cannot reach a student sheet through this page (docs/context.md §4.4, B10).

import { useQuery } from "@tanstack/react-query";
import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Suspense, useEffect } from "react";

import { AnswerKeyItemView } from "@/components/exams/print/answer-key-item";
import { SheetHeaderView } from "@/components/exams/print/sheet-header";
import { StudentSheetItemView } from "@/components/exams/print/student-sheet-item";
import { api } from "@/lib/api/client";
import type { components } from "@/lib/api/schema";
import { getUiErrorMessage } from "@/lib/api/error";
import { parsePrintParams, type PrintMode, printUrl, schoolName } from "@/lib/exams/print";
import { vi } from "@/messages/vi";

type StudentSheet = components["schemas"]["StudentSheet"];
type AnswerKey = components["schemas"]["AnswerKey"];
type Sheet = { mode: "student"; data: StudentSheet } | { mode: "answer"; data: AnswerKey };
type HttpFailure = { status: number; error: unknown };

async function fetchSheet(vid: string, mode: PrintMode): Promise<Sheet> {
  const params = { params: { path: { vid } } };
  if (mode === "student") {
    const { data, error, response } = await api.GET("/exam-versions/{vid}/student-sheet", params);
    if (error || !data) throw { status: response.status, error } satisfies HttpFailure;
    return { mode, data };
  }
  const { data, error, response } = await api.GET("/exam-versions/{vid}/answer-key", params);
  if (error || !data) throw { status: response.status, error } satisfies HttpFailure;
  return { mode, data };
}

function errorMessage(error: unknown): string {
  const status = (error as Partial<HttpFailure>)?.status;
  if (status === 404) return vi.exams.print.notFound;
  if (status === 403) return vi.exams.print.answerForbidden;
  if (status === 409) return vi.exams.print.unavailable;
  return getUiErrorMessage((error as Partial<HttpFailure>)?.error ?? error);
}

function PrintView({ vid, mode, examId }: { vid: string; mode: PrintMode; examId: string | null }) {
  const t = vi.exams.print;
  // A version can be withdrawn and resubmitted under the same vid with a new snapshot, so an export is never
  // served from cache: nothing is kept after the page closes and every open fetches again.
  const query = useQuery({
    queryKey: ["examExport", mode, vid],
    retry: false,
    staleTime: 0,
    gcTime: 0,
    refetchOnMount: "always",
    refetchOnWindowFocus: false,
    queryFn: () => fetchSheet(vid, mode),
  });
  const other: PrintMode = mode === "student" ? "answer" : "student";
  // Only print what the server currently allows: data must come from a fetch made by this page instance
  // (never a leftover cache entry), and once a refetch is refused (403/404/409) the old data is hidden.
  const sheet = query.isError || !query.isFetchedAfterMount ? undefined : query.data;

  // "Lưu PDF" uses document.title as the file name: distinct names keep the student sheet and the answer key apart.
  const docTitle = sheet
    ? `${sheet.data.header.examTitle} - ${t.versionCode} ${sheet.data.header.versionNo} - ${sheet.mode === "student" ? t.docStudent : t.docAnswer}`
    : null;
  useEffect(() => {
    if (!docTitle) return;
    const previous = document.title;
    document.title = docTitle;
    return () => {
      document.title = previous;
    };
  }, [docTitle]);

  return (
    <section className="space-y-6">
      <div className="flex flex-wrap items-center gap-3 print:hidden">
        {sheet && (
          <button type="button" onClick={() => window.print()} className="rounded bg-slate-900 px-3 py-1.5 text-sm text-white">
            {t.printButton}
          </button>
        )}
        {examId && (
          <Link href={printUrl(examId, vid, other)} className="text-sm text-blue-700 hover:underline">
            {other === "answer" ? t.toAnswer : t.toStudent}
          </Link>
        )}
        <Link href={examId ? `/exams/view/?id=${encodeURIComponent(examId)}&vid=${encodeURIComponent(vid)}` : "/exams/"} className="text-sm text-slate-600 hover:underline">
          {t.back}
        </Link>
      </div>

      {!sheet && !query.isError && <p>{vi.common.loading}</p>}
      {query.isError && <p className="text-rose-600">{errorMessage(query.error)}</p>}
      {sheet && (
        <div className="space-y-5">
          <SheetHeaderView header={sheet.data.header} mode={sheet.mode} schoolName={schoolName()} />
          {sheet.mode === "student"
            ? sheet.data.items.map((item) => <StudentSheetItemView key={item.number} item={item} />)
            : sheet.data.items.map((item) => <AnswerKeyItemView key={item.number} item={item} />)}
        </div>
      )}
    </section>
  );
}

function PrintRoute() {
  const parsed = parsePrintParams(useSearchParams());
  if (!parsed.ok) return <p className="text-rose-600">{vi.exams.print.invalidLink}</p>;
  return <PrintView vid={parsed.vid} mode={parsed.mode} examId={parsed.examId} />;
}

export default function ExamPrintPage() {
  return (
    <Suspense fallback={<p>{vi.common.loading}</p>}>
      <PrintRoute />
    </Suspense>
  );
}
