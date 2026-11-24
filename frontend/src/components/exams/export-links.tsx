import Link from "next/link";

import { printUrl } from "@/lib/exams/print";
import { vi } from "@/messages/vi";

/** Links to the print preview; the answer link only appears for readers who can see answers. */
export function ExportLinks({ examId, versionId, canReadAnswers }: { examId: string; versionId: string; canReadAnswers: boolean }) {
  const t = vi.exams.print;
  return (
    <div className="flex flex-wrap gap-3 text-sm">
      <Link href={printUrl(examId, versionId, "student")} className="rounded border border-slate-300 px-3 py-1.5 hover:bg-slate-50">
        {t.toStudent}
      </Link>
      {canReadAnswers && (
        <Link href={printUrl(examId, versionId, "answer")} className="rounded border border-slate-300 px-3 py-1.5 hover:bg-slate-50">
          {t.toAnswer}
        </Link>
      )}
    </div>
  );
}
