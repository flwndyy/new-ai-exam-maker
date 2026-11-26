"use client";

// Print preview for one exam version. Each mode calls ONLY its own endpoint: the student mode never requests
// the answer key, so answers cannot reach a student sheet through this page (docs/context.md §4.4, B10).

import { useQuery } from "@tanstack/react-query";
import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { type ReactNode, Suspense, useEffect } from "react";

import { AnswerKeyItemView } from "@/components/exams/print/answer-key-item";
import { SheetHeaderView } from "@/components/exams/print/sheet-header";
import { StudentSheetItemView } from "@/components/exams/print/student-sheet-item";
import { PageHeader } from "@/components/shell/page-header";
import { Button } from "@/components/ui/button";
import { EmptyState } from "@/components/ui/empty-state";
import { Notice } from "@/components/ui/notice";
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

/** Compact board with a way back to the exam, then the content area on a grey desk. */
function Frame({ examId, vid, children }: { examId: string | null; vid: string | null; children: ReactNode }) {
  const backHref = examId && vid ? `/exams/view/?id=${encodeURIComponent(examId)}&vid=${encodeURIComponent(vid)}` : "/exams/";
  return (
    <>
      <PageHeader title={vi.exams.print.pageTitle} crumb={{ href: backHref, label: vi.exams.print.back }} />
      {children}
    </>
  );
}

function ModeSwitch({ examId, vid, mode }: { examId: string; vid: string; mode: PrintMode }) {
  const t = vi.exams.print;
  const items: { mode: PrintMode; label: string }[] = [
    { mode: "student", label: t.docStudent },
    { mode: "answer", label: t.docAnswer },
  ];
  return (
    <nav aria-label={t.modeSwitch} className="inline-flex overflow-hidden rounded-md border border-line-strong">
      {items.map((item) => (
        <Link
          key={item.mode}
          href={printUrl(examId, vid, item.mode)}
          aria-current={item.mode === mode ? "page" : undefined}
          className={
            item.mode === mode
              ? "bg-accent px-3 py-1.5 text-[13px] font-semibold text-white"
              : "px-3 py-1.5 text-[13px] text-ink transition-colors duration-150 hover:bg-tint"
          }
        >
          {item.label}
        </Link>
      ))}
    </nav>
  );
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
    <Frame examId={examId} vid={vid}>
      <div data-testid="print-toolbar" className="flex flex-wrap items-center gap-2.5 border-b border-line bg-white px-4 py-3 sm:px-7 print:hidden">
        {examId && <ModeSwitch examId={examId} vid={vid} mode={mode} />}
        {sheet && (
          <Button className="ml-auto" onClick={() => window.print()}>
            {t.printButton}
          </Button>
        )}
      </div>

      <div className="bg-desk p-3 sm:p-7 print:bg-white print:p-0">
        {!sheet && !query.isError && <EmptyState busy title={vi.common.loading} />}
        {query.isError && <Notice tone="error">{errorMessage(query.error)}</Notice>}
        {sheet && (
          <div
            data-testid="print-sheet"
            className="print-sheet mx-auto w-full max-w-[700px] bg-white px-5 py-6 font-serif text-[14px] leading-[1.5] text-black shadow-sheet sm:px-[52px] sm:py-11 print:max-w-none print:p-0 print:shadow-none"
          >
            <SheetHeaderView header={sheet.data.header} mode={sheet.mode} schoolName={schoolName()} />
            {sheet.mode === "student"
              ? sheet.data.items.map((item) => <StudentSheetItemView key={item.number} item={item} />)
              : sheet.data.items.map((item) => <AnswerKeyItemView key={item.number} item={item} />)}
          </div>
        )}
      </div>
    </Frame>
  );
}

function PrintRoute() {
  const parsed = parsePrintParams(useSearchParams());
  if (!parsed.ok) {
    return (
      <Frame examId={null} vid={null}>
        <div className="px-4 py-5 sm:px-7 sm:py-6">
          <Notice tone="error">{vi.exams.print.invalidLink}</Notice>
        </div>
      </Frame>
    );
  }
  return <PrintView vid={parsed.vid} mode={parsed.mode} examId={parsed.examId} />;
}

export default function ExamPrintPage() {
  return (
    <Suspense
      fallback={
        <Frame examId={null} vid={null}>
          <div className="px-4 py-5 sm:px-7 sm:py-6">
            <EmptyState busy title={vi.common.loading} />
          </div>
        </Frame>
      }
    >
      <PrintRoute />
    </Suspense>
  );
}
