"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import { useMutation, useQuery } from "@tanstack/react-query";
import { useId, useState } from "react";
import { useForm } from "react-hook-form";
import { z } from "zod";

import { Button } from "@/components/ui/button";
import { Field, Input, Select } from "@/components/ui/field";
import { Notice } from "@/components/ui/notice";
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

  const ids = { title: useId(), subject: useId(), grade: useId() };

  return (
    <form
      noValidate
      aria-label={vi.exams.createTitle}
      onSubmit={handleSubmit((body) => {
        setFormError(null);
        mutation.mutate(body);
      })}
      className="mb-5 grid grid-cols-1 gap-x-3.5 rounded-lg border border-line bg-white p-[18px] pb-1.5 sm:grid-cols-3"
    >
      <h2 className="mb-3 text-[15px] font-semibold sm:col-span-3">{vi.exams.createTitle}</h2>
      <Field label={vi.exams.titleLabel} htmlFor={ids.title} error={errors.title?.message} className="sm:col-span-3">
        <Input id={ids.title} {...register("title")} maxLength={300} />
      </Field>
      <Field label={vi.exams.subject} htmlFor={ids.subject} error={errors.subjectId?.message}>
        <Select id={ids.subject} {...register("subjectId")}>
          <option value="">{vi.exams.chooseSubject}</option>
          {subjectsQuery.data?.map((s) => (
            <option key={s.id} value={s.id}>
              {s.name}
            </option>
          ))}
        </Select>
      </Field>
      <Field label={vi.exams.grade} htmlFor={ids.grade} error={errors.grade?.message}>
        <Select id={ids.grade} {...register("grade")}>
          <option value="">{vi.exams.chooseGrade}</option>
          {["10", "11", "12"].map((g) => (
            <option key={g} value={g}>
              {g}
            </option>
          ))}
        </Select>
      </Field>
      <div className="mb-3.5 flex items-end gap-2">
        <Button type="submit" disabled={mutation.isPending}>
          {mutation.isPending ? vi.common.saving : vi.exams.createTitle}
        </Button>
        <Button variant="secondary" onClick={onCancel}>
          {vi.exams.cancel}
        </Button>
      </div>
      {formError && (
        <Notice tone="error" className="mb-3.5 sm:col-span-3">
          {formError}
        </Notice>
      )}
    </form>
  );
}
