"use client";

import { useQuery } from "@tanstack/react-query";
import { useRouter } from "next/navigation";
import { useMemo, useState } from "react";

import { CreateExamForm } from "@/components/exams/create-exam-form";
import { PageHeader } from "@/components/shell/page-header";
import { Button } from "@/components/ui/button";
import { EmptyState } from "@/components/ui/empty-state";
import { Checkbox } from "@/components/ui/field";
import { FilterBar, FilterSelect } from "@/components/ui/filter-bar";
import { ListRow } from "@/components/ui/list-row";
import { Notice } from "@/components/ui/notice";
import { Pager } from "@/components/ui/pager";
import { StatusText } from "@/components/ui/status-text";
import { fetchAllSubjects } from "@/lib/api/catalog";
import { api } from "@/lib/api/client";
import { getUiErrorMessage } from "@/lib/api/error";
import type { components } from "@/lib/api/schema";
import { useAuth } from "@/lib/auth/auth-context";
import { examVersionTone } from "@/lib/ui/status";
import { vi } from "@/messages/vi";

type Grade = components["schemas"]["Grade"];

const PAGE_SIZE = 20;
const dateFormat = new Intl.DateTimeFormat("vi-VN", {
  timeZone: "Asia/Ho_Chi_Minh",
  day: "2-digit",
  month: "2-digit",
  year: "numeric",
});

export default function ExamsPage() {
  const { me } = useAuth();
  const router = useRouter();
  const [page, setPage] = useState(1);
  const [subjectId, setSubjectId] = useState("");
  const [grade, setGrade] = useState("");
  const [mine, setMine] = useState(false);
  const [creating, setCreating] = useState(false);

  const canCreate = (me?.role === "teacher" || me?.role === "head") && Boolean(me?.departmentId);

  const subjectsQuery = useQuery({ queryKey: ["catalog", "subjects", "all"], queryFn: () => fetchAllSubjects() });
  const subjectNames = useMemo(() => new Map(subjectsQuery.data?.map((s) => [s.id, s.name])), [subjectsQuery.data]);

  const filters = { page, pageSize: PAGE_SIZE, subjectId: subjectId || undefined, grade: grade ? (Number(grade) as Grade) : undefined, mine: mine || undefined };
  const examsQuery = useQuery({
    queryKey: ["exams", filters],
    queryFn: async () => {
      const { data, error } = await api.GET("/exams", { params: { query: filters } });
      if (error || !data) throw error ?? new Error(vi.common.requestFailed);
      return data;
    },
  });

  return (
    <>
      <PageHeader
        title={vi.exams.listTitle}
        actions={
          canCreate && !creating ? (
            <Button variant="boardPrimary" onClick={() => setCreating(true)}>
              {vi.exams.create}
            </Button>
          ) : null
        }
      />
      <div className="px-4 py-5 sm:px-7 sm:py-6">
        {creating && me?.departmentId && (
          <CreateExamForm
            departmentId={me.departmentId}
            onCancel={() => setCreating(false)}
            onCreated={(exam) => router.push(`/exams/view/?id=${exam.id}&vid=${exam.versions[0]?.id ?? ""}`)}
          />
        )}

        <FilterBar>
          <FilterSelect
            label={vi.exams.subject}
            value={subjectId}
            onChange={(e) => {
              setSubjectId(e.target.value);
              setPage(1);
            }}
          >
            <option value="">{vi.exams.allSubjects}</option>
            {subjectsQuery.data?.map((s) => (
              <option key={s.id} value={s.id}>
                {s.name}
              </option>
            ))}
          </FilterSelect>
          <FilterSelect
            label={vi.exams.grade}
            value={grade}
            onChange={(e) => {
              setGrade(e.target.value);
              setPage(1);
            }}
          >
            <option value="">{vi.exams.allGrades}</option>
            {["10", "11", "12"].map((g) => (
              <option key={g} value={g}>
                {`${vi.exams.grade} ${g}`}
              </option>
            ))}
          </FilterSelect>
          <Checkbox
            label={vi.exams.mineOnly}
            checked={mine}
            onChange={(e) => {
              setMine(e.target.checked);
              setPage(1);
            }}
          />
        </FilterBar>

        {examsQuery.isLoading && <EmptyState busy title={vi.common.loading} />}
        {examsQuery.isError && <Notice tone="error">{getUiErrorMessage(examsQuery.error)}</Notice>}
        {examsQuery.data && examsQuery.data.items.length === 0 && <EmptyState title={vi.exams.empty} />}

        {examsQuery.data?.items.map((exam) => (
          <ListRow
            key={exam.id}
            tone={examVersionTone(exam.latestVersionStatus)}
            href={`/exams/view/?id=${exam.id}`}
            title={exam.title}
            meta={vi.exams.listMeta(subjectNames.get(exam.subjectId), exam.grade, exam.createdBy.fullName, dateFormat.format(new Date(exam.updatedAt)))}
            end={
              <>
                <StatusText tone={examVersionTone(exam.latestVersionStatus)}>
                  {vi.exams.latestVersion(exam.latestVersionNo, vi.review.examStatuses[exam.latestVersionStatus])}
                </StatusText>
                {exam.publishedVersionNo !== null && (
                  <span className="text-[12.5px] font-semibold text-pub">{vi.exams.publishedVersion(exam.publishedVersionNo)}</span>
                )}
              </>
            }
          />
        ))}

        {examsQuery.data && examsQuery.data.totalPages > 1 && (
          <Pager page={page} totalPages={examsQuery.data.totalPages} onPage={setPage} />
        )}
      </div>
    </>
  );
}
