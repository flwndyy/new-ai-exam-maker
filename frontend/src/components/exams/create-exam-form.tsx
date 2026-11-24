"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import { useMutation, useQuery } from "@tanstack/react-query";
import { useState } from "react";
import { useForm } from "react-hook-form";
import { z } from "zod";

import { fetchAllSubjects } from "@/lib/api/catalog";
import { api } from "@/lib/api/client";
import { getApiErrorMessage, getUiErrorMessage } from "@/lib/api/error";
import type { components } from "@/lib/api/schema";
import { vi } from "@/messages/vi";

type ExamDetail = components["schemas"]["ExamDetail"];
type Grade = components["schemas"]["Grade"];

const FIELDS = ["title", "subjectId", "grade"] as const;

const schema = z.object({
  title: z
    .string()
    .transform((s) => s.trim())
    .pipe(z.string().min(1, vi.exams.validation.titleRequired).max(300, vi.exams.validation.titleRequired)),
  subjectId: z.string().min(1, vi.exams.validation.required),
  grade: z.enum(["10", "11", "12"], { error: vi.exams.validation.required }).transform((g) => Number(g) as Grade),
});

type Input = z.input<typeof schema>;
type Output = z.output<typeof schema>;

/** Create an exam (with its empty Draft v1) in one of the author's own department subjects. */
export function CreateExamForm({
  departmentId,
  onCreated,
  onCancel,
}: {
  departmentId: string;
  onCreated: (exam: ExamDetail) => void;
  onCancel: () => void;
}) {
  const [formError, setFormError] = useState<string | null>(null);
  const {
    register,
    handleSubmit,
    setError,
    formState: { errors },
  } = useForm<Input, unknown, Output>({
    resolver: zodResolver(schema),
    defaultValues: { title: "", subjectId: "", grade: "" as Input["grade"] },
  });

  const subjectsQuery = useQuery({
    queryKey: ["catalog", "subjects", departmentId],
    queryFn: () => fetchAllSubjects(departmentId),
  });

  const mutation = useMutation({
    mutationFn: async (body: Output) => {
      const { data, error } = await api.POST("/exams", { body });
      if (error || !data) throw error ?? new Error(vi.common.requestFailed);
      return data;
    },
    onSuccess: onCreated,
    onError: (error: unknown) => {
      const fieldErrors = (error as { errors?: Record<string, string[]> } | undefined)?.errors ?? {};
      let unmatched = Object.keys(fieldErrors).length === 0;
      for (const [field, list] of Object.entries(fieldErrors)) {
        if ((FIELDS as readonly string[]).includes(field)) {
          setError(field as (typeof FIELDS)[number], { message: list.join(", ") });
        } else {
          unmatched = true;
        }
      }
      setFormError(unmatched ? (error instanceof Error ? getUiErrorMessage(error) : getApiErrorMessage(error)) : null);
    },
  });

  const inputClass = "w-full rounded border border-slate-300 bg-white px-2 py-1.5 text-sm";

  return (
    <form
      noValidate
      aria-label={vi.exams.createTitle}
      onSubmit={handleSubmit((body) => {
        setFormError(null);
        mutation.mutate(body);
      })}
      className="grid grid-cols-1 gap-3 rounded border border-slate-200 bg-slate-50 p-4 sm:grid-cols-3"
    >
      <label className="space-y-1 text-sm sm:col-span-3">
        <span className="font-medium text-slate-700">{vi.exams.titleLabel}</span>
        <input {...register("title")} className={inputClass} maxLength={300} />
        {errors.title && <span className="block text-xs text-rose-600">{errors.title.message}</span>}
      </label>
      <label className="space-y-1 text-sm">
        <span className="font-medium text-slate-700">{vi.exams.subject}</span>
        <select {...register("subjectId")} className={inputClass}>
          <option value="">{vi.exams.chooseSubject}</option>
          {subjectsQuery.data?.map((s) => (
            <option key={s.id} value={s.id}>
              {s.name}
            </option>
          ))}
        </select>
        {errors.subjectId && <span className="block text-xs text-rose-600">{errors.subjectId.message}</span>}
      </label>
      <label className="space-y-1 text-sm">
        <span className="font-medium text-slate-700">{vi.exams.grade}</span>
        <select {...register("grade")} className={inputClass}>
          <option value="">{vi.exams.chooseGrade}</option>
          {["10", "11", "12"].map((g) => (
            <option key={g} value={g}>
              {g}
            </option>
          ))}
        </select>
        {errors.grade && <span className="block text-xs text-rose-600">{errors.grade.message}</span>}
      </label>
      <div className="flex items-end gap-2">
        <button
          type="submit"
          disabled={mutation.isPending}
          className="rounded bg-slate-900 px-4 py-2 text-sm font-medium text-white hover:bg-slate-800 disabled:opacity-50"
        >
          {mutation.isPending ? vi.common.saving : vi.exams.createTitle}
        </button>
        <button type="button" onClick={onCancel} className="rounded border border-slate-300 px-4 py-2 text-sm">
          {vi.exams.cancel}
        </button>
      </div>
      {formError && <p className="text-sm text-rose-600 sm:col-span-3">{formError}</p>}
    </form>
  );
}
