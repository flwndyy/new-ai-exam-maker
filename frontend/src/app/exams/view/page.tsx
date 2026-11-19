"use client";

// Dynamic resources use a query string (/exams/view/?id=...) instead of /exams/[id],
// because static export must know every route at build time (docs/context.md §9.2).

import { useSearchParams } from "next/navigation";
import { Suspense } from "react";

import { vi } from "@/messages/vi";

function ExamView() {
  const id = useSearchParams().get("id");

  return (
    <section>
      <h1 className="text-2xl font-bold">{vi.examView.title}</h1>
      {id ? (
        <p className="mt-2 text-slate-600">
          {vi.examView.currentId}: <code>{id}</code>
        </p>
      ) : (
        <p className="mt-2 text-red-600">{vi.examView.missingId}</p>
      )}
    </section>
  );
}

export default function ExamViewPage() {
  return (
    <Suspense fallback={<p>{vi.common.loading}</p>}>
      <ExamView />
    </Suspense>
  );
}
