import type { components } from "@/lib/api/schema";
import type { PrintMode } from "@/lib/exams/print";
import { formatDecimalForInput } from "@/lib/format/decimal";
import { vi } from "@/messages/vi";

type SheetHeader = components["schemas"]["SheetHeader"];

/** Exam header shared by the student sheet and the answer key; all values render as plain text, black on white. */
export function SheetHeaderView({ header, mode, schoolName }: { header: SheetHeader; mode: PrintMode; schoolName: string | null }) {
  const t = vi.exams.print;
  return (
    <header className="mb-5 border-b border-black pb-4">
      <div className="grid grid-cols-1 gap-3 text-center sm:grid-cols-[1fr_1.3fr]">
        <div>
          {schoolName && <p>{schoolName}</p>}
          {header.departmentName && (
            <p>
              <span className="inline-block border-b border-black pb-0.5">{`${t.department} ${header.departmentName}`}</span>
            </p>
          )}
        </div>
        <div>
          <p className="text-[15px] font-bold">{mode === "student" ? t.studentTitle : t.answerTitle}</p>
          <p>{t.subjectLine(header.subjectName, header.grade)}</p>
          <p>
            {t.duration}: {header.durationMinutes} {t.minutes}
          </p>
          <p className="mt-1.5">
            <span className="inline-block border border-black px-2.5 py-0.5 font-bold">
              {t.versionCode} {header.versionNo}
            </span>
          </p>
          <p className="mt-1">
            {t.totalPoints}: {formatDecimalForInput(header.totalPoints)}
          </p>
        </div>
      </div>
      <p className="mt-4 text-center text-[17px] font-bold">{header.examTitle}</p>
      {mode === "student" && (
        <p className="mt-3">
          {t.studentName}: ............................................................ {t.studentClass}: ..................
        </p>
      )}
    </header>
  );
}
