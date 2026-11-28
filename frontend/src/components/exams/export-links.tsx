import { ButtonLink } from "@/components/ui/button";
import { printUrl } from "@/lib/exams/print";
import { vi } from "@/messages/vi";

/** Links to the print preview; the answer link only appears for readers who can see answers. */
export function ExportLinks({ examId, versionId, canReadAnswers }: { examId: string; versionId: string; canReadAnswers: boolean }) {
  const t = vi.exams.print;
  return (
    <>
      <ButtonLink href={printUrl(examId, versionId, "student")} variant="secondary" size="sm">
        {t.toStudent}
      </ButtonLink>
      {canReadAnswers && (
        <ButtonLink href={printUrl(examId, versionId, "answer")} variant="secondary" size="sm">
          {t.toAnswer}
        </ButtonLink>
      )}
    </>
  );
}
