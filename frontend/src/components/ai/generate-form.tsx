"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useRouter } from "next/navigation";
import { useId, useMemo, useState } from "react";
import { Controller, useForm, useWatch } from "react-hook-form";

import {
  DIFFICULTIES,
  GRADES,
  type GenerateFormInput,
  type GenerateFormOutput,
  makeGenerateSchema,
  MAX_NOTE_LENGTH,
  QUESTION_TYPES,
} from "@/lib/ai/generate-schema";
import { fetchAllSubjects, fetchAllTopics } from "@/lib/api/catalog";
import { api } from "@/lib/api/client";
import { getApiErrorCode, getApiErrorMessage } from "@/lib/api/error";
import type { components } from "@/lib/api/schema";
import { vi } from "@/messages/vi";

type AiQuota = components["schemas"]["AiQuota"];
type AiGenerateRequest = components["schemas"]["AiGenerateRequest"];
type Grade = components["schemas"]["Grade"];

const FIELDS = ["subjectId", "grade", "topicId", "type", "difficulty", "count", "note"] as const;
type Field = (typeof FIELDS)[number];

const numberFormat = new Intl.NumberFormat("vi-VN");
const resetFormat = new Intl.DateTimeFormat("vi-VN", {
  hour: "2-digit",
  minute: "2-digit",
  day: "2-digit",
  month: "2-digit",
  timeZone: "Asia/Ho_Chi_Minh",
});

const inputClass = "mt-1 w-full rounded border border-slate-300 px-3 py-2 text-sm";
const errorClass = "mt-1 text-xs text-red-600";

function toFormValues(initial?: Partial<AiGenerateRequest>): GenerateFormInput {
  return {
    subjectId: initial?.subjectId ?? "",
    grade: (initial?.grade ? String(initial.grade) : "10") as GenerateFormInput["grade"],
    topicId: initial?.topicId ?? "",
    type: initial?.type ?? "MultipleChoice",
    difficulty: initial?.difficulty ?? "Recognition",
    count: String(initial?.count ?? 5),
    note: initial?.note ?? "",
  };
}

function quotaBlock(quota: AiQuota | undefined): string | null {
  if (!quota) return null;
  if (quota.jobsUsed >= quota.jobsLimit) return vi.ai.quotaJobsExhausted;
  if (quota.tokensUsed >= quota.tokensLimit) return vi.ai.quotaTokensExhausted;
  return null;
}

/**
 * AI generation request form. Subjects are limited to the requester's department because the API
 * rejects other subjects (docs/context.md 4.1). The note warning is always visible (B8 acceptance).
 */
export function GenerateForm({
  departmentId,
  quota,
  initial,
}: {
  departmentId: string;
  quota: AiQuota | undefined;
  initial?: Partial<AiGenerateRequest>;
}) {
  const router = useRouter();
  const queryClient = useQueryClient();
  const warningId = useId();
  const counterId = useId();
  const [formError, setFormError] = useState<string | null>(null);

  const maxQuestions = quota?.maxQuestionsPerJob ?? 10;
  const schema = useMemo(() => makeGenerateSchema(maxQuestions), [maxQuestions]);
  const {
    control,
    register,
    handleSubmit,
    setValue,
    setError,
    formState: { errors },
  } = useForm<GenerateFormInput, unknown, GenerateFormOutput>({
    resolver: zodResolver(schema),
    defaultValues: toFormValues(initial),
  });

  const subjectId = useWatch({ control, name: "subjectId" });
  const grade = useWatch({ control, name: "grade" });
  const note = useWatch({ control, name: "note" }) ?? "";

  const subjectsQuery = useQuery({
    queryKey: ["catalog", "subjects", departmentId],
    queryFn: () => fetchAllSubjects(departmentId),
  });

  const topicsQuery = useQuery({
    queryKey: ["catalog", "topics", subjectId, grade],
    enabled: Boolean(subjectId && grade),
    queryFn: () => fetchAllTopics(subjectId, Number(grade) as Grade),
  });

  const mutation = useMutation({
    mutationFn: async (body: GenerateFormOutput) => {
      const { data, error, response } = await api.POST("/ai/generate", { body });
      if (error || !data) {
        throw { error, status: response.status, retryAfter: response.headers.get("Retry-After") };
      }
      return data;
    },
    onSuccess: async (job) => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ["ai", "quota"] }),
        queryClient.invalidateQueries({ queryKey: ["ai", "jobs"] }),
      ]);
      router.push(`/ai/job/?id=${job.id}`);
    },
    onError: (failure: unknown) => {
      const { error, status, retryAfter } = (failure ?? {}) as { error?: unknown; status?: number; retryAfter?: string | null };
      if (status === 429) {
        const seconds = retryAfter && /^\d+$/.test(retryAfter) ? Number(retryAfter) : null;
        setFormError(getApiErrorCode(error) === "QuotaExceeded" ? vi.ai.quotaExceeded : vi.ai.rateLimited(seconds));
        void queryClient.invalidateQueries({ queryKey: ["ai", "quota"] });
        return;
      }
      if (status === 403) {
        setFormError(vi.ai.forbidden);
        return;
      }
      const fieldErrors = (error as { errors?: Record<string, string[]> } | undefined)?.errors ?? {};
      let unmatched = false;
      for (const [field, list] of Object.entries(fieldErrors)) {
        if ((FIELDS as readonly string[]).includes(field)) {
          setError(field as Field, { message: list.join(", ") });
        } else {
          unmatched = true;
        }
      }
      setFormError(Object.keys(fieldErrors).length === 0 || unmatched ? getApiErrorMessage(error) : null);
    },
  });

  const blocked = quotaBlock(quota);
  const onSubmit = handleSubmit((body) => {
    setFormError(null);
    mutation.mutate(body);
  });

  return (
    <form onSubmit={onSubmit} noValidate className="space-y-4 rounded border border-slate-200 bg-white p-4">
      <h2 className="text-lg font-semibold text-slate-900">{vi.ai.formTitle}</h2>

      {quota && (
        <p className="text-sm text-slate-600">
          {vi.ai.quota(
            quota.jobsUsed,
            quota.jobsLimit,
            numberFormat.format(quota.tokensUsed),
            numberFormat.format(quota.tokensLimit),
            resetFormat.format(new Date(quota.resetsAt)),
          )}
        </p>
      )}

      <div className="grid gap-4 sm:grid-cols-3">
        <label className="block text-sm font-medium text-slate-700">
          {vi.ai.subject}
          <Controller
            control={control}
            name="subjectId"
            render={({ field }) => (
              <select
                {...field}
                className={inputClass}
                onChange={(e) => {
                  field.onChange(e.target.value);
                  setValue("topicId", "");
                }}
              >
                <option value="">{vi.ai.choose}</option>
                {(subjectsQuery.data ?? []).map((s) => (
                  <option key={s.id} value={s.id}>
                    {s.name}
                  </option>
                ))}
              </select>
            )}
          />
          {errors.subjectId && <span className={errorClass}>{errors.subjectId.message}</span>}
        </label>

        <label className="block text-sm font-medium text-slate-700">
          {vi.ai.grade}
          <select
            {...register("grade", { onChange: () => setValue("topicId", "") })}
            className={inputClass}
          >
            {GRADES.map((g) => (
              <option key={g} value={g}>
                {g}
              </option>
            ))}
          </select>
          {errors.grade && <span className={errorClass}>{errors.grade.message}</span>}
        </label>

        <label className="block text-sm font-medium text-slate-700">
          {vi.ai.topic}
          <Controller
            control={control}
            name="topicId"
            render={({ field }) => (
              <select {...field} className={inputClass} disabled={!subjectId}>
                <option value="">{vi.ai.choose}</option>
                {(topicsQuery.data ?? []).map((t) => (
                  <option key={t.id} value={t.id}>
                    {t.name}
                  </option>
                ))}
              </select>
            )}
          />
          {errors.topicId && <span className={errorClass}>{errors.topicId.message}</span>}
        </label>

        <label className="block text-sm font-medium text-slate-700">
          {vi.ai.type}
          <select {...register("type")} className={inputClass}>
            {QUESTION_TYPES.map((t) => (
              <option key={t} value={t}>
                {vi.questions.types[t]}
              </option>
            ))}
          </select>
        </label>

        <label className="block text-sm font-medium text-slate-700">
          {vi.ai.difficulty}
          <select {...register("difficulty")} className={inputClass}>
            {DIFFICULTIES.map((d) => (
              <option key={d} value={d}>
                {vi.questions.difficulties[d]}
              </option>
            ))}
          </select>
        </label>

        <div className="text-sm">
          <label htmlFor="ai-count" className="font-medium text-slate-700">
            {vi.ai.count}
          </label>{" "}
          <span className="text-slate-500">({vi.ai.countHint(maxQuestions)})</span>
          <input
            id="ai-count"
            type="number"
            min={1}
            max={maxQuestions}
            step={1}
            {...register("count")}
            className={inputClass}
          />
          {errors.count && <span className={errorClass}>{errors.count.message}</span>}
        </div>
      </div>

      <div>
        <label htmlFor="ai-note" className="block text-sm font-medium text-slate-700">
          {vi.ai.note}
        </label>
        <textarea
          id="ai-note"
          rows={4}
          maxLength={MAX_NOTE_LENGTH}
          aria-describedby={`${warningId} ${counterId}`}
          {...register("note")}
          className={inputClass}
        />
        <p id={warningId} role="note" className="mt-2 rounded border border-amber-300 bg-amber-50 px-3 py-2 text-sm text-amber-900">
          {vi.ai.noteWarning}
        </p>
        <p id={counterId} className="mt-1 text-right text-xs text-slate-500">
          {vi.ai.noteCounter(note.length)}
        </p>
        {errors.note && <span className={errorClass}>{errors.note.message}</span>}
      </div>

      {blocked && <p className="text-sm text-red-700">{blocked}</p>}
      {formError && (
        <p role="alert" className="text-sm text-red-700">
          {formError}
        </p>
      )}

      <div className="flex justify-end">
        <button
          type="submit"
          disabled={Boolean(blocked) || mutation.isPending}
          className="rounded bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50"
        >
          {mutation.isPending ? vi.ai.submitting : vi.ai.submit}
        </button>
      </div>
    </form>
  );
}
