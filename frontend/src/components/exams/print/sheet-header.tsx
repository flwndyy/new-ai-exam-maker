import type { components } from "@/lib/api/schema";
import type { PrintMode } from "@/lib/exams/print";
import { formatDecimalForInput } from "@/lib/format/decimal";
import { vi } from "@/messages/vi";

type SheetHeader = components["schemas"]["SheetHeader"];

/** Exam header shared by the student sheet and the answer key; all values render as plain text. */
export function SheetHeaderView({ header, mode, schoolName }: { header: SheetHeader; mode: PrintMode; schoolName: string | null }) {
  const t = vi.exams.print;
  return (
    <header className="mb-6 space-y-3 border-b border-slate-300 pb-4">
      <div className="grid grid-cols-2 gap-4 text-sm">
        <div className="space-y-1">
          {schoolName && <p className="font-semibold uppercase">{schoolName}</p>}
          {header.departmentName && (
            <p>
              {t.department}: {header.departmentName}
            </p>
          )}
        </div>
        <div className="space-y-1 text-right">
          <p className="text-base font-bold">{mode === "student" ? t.studentTitle : t.answerTitle}</p>
          <p>
            {t.subject}: {header.subjectName} — {t.grade} {header.grade}
          </p>
          <p>
            {t.duration}: {header.durationMinutes} {t.minutes}
          </p>
          <p>
            {t.versionCode}: {header.versionNo} · {t.totalPoints}: {formatDecimalForInput(header.totalPoints)}
          </p>
        </div>
      </div>
      <p className="text-center text-lg font-semibold">{header.examTitle}</p>
      {mode === "student" && (
        <p className="text-sm">
          {t.studentName}: ............................................................ {t.studentClass}: ..................
        </p>
      )}
    </header>
  );
}
