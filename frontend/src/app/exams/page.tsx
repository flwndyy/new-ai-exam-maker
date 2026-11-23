"use client";

import { useQuery } from "@tanstack/react-query";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useMemo, useState } from "react";

import { CreateExamForm } from "@/components/exams/create-exam-form";
import { fetchAllSubjects } from "@/lib/api/catalog";
import { api } from "@/lib/api/client";
import { getUiErrorMessage } from "@/lib/api/error";
import type { components } from "@/lib/api/schema";
import { useAuth } from "@/lib/auth/auth-context";
import { vi } from "@/messages/vi";

type Grade = components["schemas"]["Grade"];

const PAGE_SIZE = 20;
const dateFormat = new Intl.DateTimeFormat("vi-VN", {
  timeZone: "Asia/Ho_Chi_Minh",
  dateStyle: "short",
  timeStyle: "short",
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

  const selectClass = "rounded border border-slate-300 bg-white px-2 py-1.5 text-sm";

  return (
    <section className="space-y-6">
      <div className="flex flex-wrap items-center justify-between gap-4">
        <h1 className="text-2xl font-bold text-slate-900">{vi.exams.listTitle}</h1>
        {canCreate && !creating && (
          <button
            type="button"
            onClick={() => setCreating(true)}
            className="rounded bg-slate-900 px-4 py-2 text-sm font-medium text-white hover:bg-slate-800"
          >
            {vi.exams.create}
          </button>
        )}
      </div>

      {creating && me?.departmentId && (
        <CreateExamForm
          departmentId={me.departmentId}
          onCancel={() => setCreating(false)}
          onCreated={(exam) => router.push(`/exams/view/?id=${exam.id}&vid=${exam.versions[0]?.id ?? ""}`)}
        />
      )}

      <div className="flex flex-wrap items-center gap-3 rounded border border-slate-200 bg-slate-50 p-4">
        <select
          aria-label={vi.exams.subject}
          value={subjectId}
          onChange={(e) => {
            setSubjectId(e.target.value);
            setPage(1);
          }}
          className={selectClass}
        >
          <option value="">{vi.exams.allSubjects}</option>
          {subjectsQuery.data?.map((s) => (
            <option key={s.id} value={s.id}>
              {s.name}
            </option>
          ))}
        </select>
        <select
          aria-label={vi.exams.grade}
          value={grade}
          onChange={(e) => {
            setGrade(e.target.value);
            setPage(1);
          }}
          className={selectClass}
        >
          <option value="">{vi.exams.allGrades}</option>
          {["10", "11", "12"].map((g) => (
            <option key={g} value={g}>
              {g}
            </option>
          ))}
        </select>
        <label className="flex items-center gap-2 text-sm text-slate-700">
          <input
            type="checkbox"
            checked={mine}
            onChange={(e) => {
              setMine(e.target.checked);
              setPage(1);
            }}
          />
          {vi.exams.mineOnly}
        </label>
      </div>

      {examsQuery.isLoading && <p className="text-sm text-slate-600">{vi.common.loading}</p>}
      {examsQuery.isError && <p className="text-sm text-rose-600">{getUiErrorMessage(examsQuery.error)}</p>}
      {examsQuery.data && examsQuery.data.items.length === 0 && (
        <p className="rounded border border-slate-200 p-6 text-center text-sm text-slate-500">{vi.exams.empty}</p>
      )}

      {examsQuery.data && examsQuery.data.items.length > 0 && (
        <div className="overflow-x-auto rounded border border-slate-200 bg-white">
          <table className="min-w-full text-sm">
            <thead className="bg-slate-50 text-left text-slate-600">
              <tr>
                <th className="px-3 py-2">{vi.exams.titleLabel}</th>
                <th className="px-3 py-2">{vi.exams.subject}</th>
                <th className="px-3 py-2">{vi.exams.grade}</th>
                <th className="px-3 py-2">{vi.exams.author}</th>
                <th className="px-3 py-2">{vi.exams.latestVersion}</th>
                <th className="px-3 py-2">{vi.exams.updatedAt}</th>
              </tr>
            </thead>
            <tbody>
              {examsQuery.data.items.map((exam) => (
                <tr key={exam.id} className="border-t border-slate-100">
                  <td className="px-3 py-2">
                    <Link href={`/exams/view/?id=${exam.id}`} className="font-medium text-slate-900 underline-offset-2 hover:underline">
                      {exam.title}
                    </Link>
                  </td>
                  <td className="px-3 py-2">{subjectNames.get(exam.subjectId) ?? "—"}</td>
                  <td className="px-3 py-2">{exam.grade}</td>
                  <td className="px-3 py-2">{exam.createdBy.fullName}</td>
                  <td className="px-3 py-2">
                    <span>{`v${exam.latestVersionNo}`}</span>
                    {exam.publishedVersionId && (
                      <span className="ml-2 rounded bg-emerald-100 px-2 py-0.5 text-xs text-emerald-800">{vi.exams.published}</span>
                    )}
                  </td>
                  <td className="px-3 py-2 text-slate-600">{dateFormat.format(new Date(exam.updatedAt))}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {examsQuery.data && examsQuery.data.totalPages > 1 && (
        <div className="flex items-center justify-center gap-3 text-sm">
          <button type="button" disabled={page <= 1} onClick={() => setPage((p) => p - 1)} className="rounded border px-3 py-1 disabled:opacity-40">
            {vi.exams.prevPage}
          </button>
          <span>{vi.exams.pageInfo(page, examsQuery.data.totalPages)}</span>
          <button
            type="button"
            disabled={page >= examsQuery.data.totalPages}
            onClick={() => setPage((p) => p + 1)}
            className="rounded border px-3 py-1 disabled:opacity-40"
          >
            {vi.exams.nextPage}
          </button>
        </div>
      )}
    </section>
  );
}
