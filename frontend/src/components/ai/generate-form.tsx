"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useRouter } from "next/navigation";
import { useId, useMemo, useState } from "react";
import { Controller, useForm, useWatch } from "react-hook-form";

import { Button } from "@/components/ui/button";
import { Field, Input, Select, Textarea } from "@/components/ui/field";
import { Notice } from "@/components/ui/notice";
import { Panel } from "@/components/ui/panel";
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

/** "Hôm nay còn 7/10 lượt ..." for the page header; remaining counts are clamped at 0. */
export function formatQuotaLine(quota: AiQuota): string {
  return vi.ai.quota(
    Math.max(quota.jobsLimit - quota.jobsUsed, 0),
    quota.jobsLimit,
    numberFormat.format(Math.max(quota.tokensLimit - quota.tokensUsed, 0)),
    numberFormat.format(quota.tokensLimit),
    resetFormat.format(new Date(quota.resetsAt)),
  );
}

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
    <Panel title={vi.ai.formTitle}>
      <form onSubmit={onSubmit} noValidate>
        <div className="grid gap-x-3.5 sm:grid-cols-2">
          <Field label={vi.ai.subject} htmlFor="ai-subject" error={errors.subjectId?.message}>
            <Controller
              control={control}
              name="subjectId"
              render={({ field }) => (
                <Select
                  id="ai-subject"
                  {...field}
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
                </Select>
              )}
            />
          </Field>

          <Field label={vi.ai.grade} htmlFor="ai-grade" error={errors.grade?.message}>
            <Select id="ai-grade" {...register("grade", { onChange: () => setValue("topicId", "") })}>
              {GRADES.map((g) => (
                <option key={g} value={g}>
                  {g}
                </option>
              ))}
            </Select>
          </Field>
        </div>

        <Field label={vi.ai.topic} htmlFor="ai-topic" error={errors.topicId?.message}>
          <Controller
            control={control}
            name="topicId"
            render={({ field }) => (
              <Select id="ai-topic" {...field} disabled={!subjectId}>
                <option value="">{vi.ai.choose}</option>
                {(topicsQuery.data ?? []).map((t) => (
                  <option key={t.id} value={t.id}>
                    {t.name}
                  </option>
                ))}
              </Select>
            )}
          />
        </Field>

        <div className="grid gap-x-3.5 sm:grid-cols-2">
          <Field label={vi.ai.type} htmlFor="ai-type">
            <Select id="ai-type" {...register("type")}>
              {QUESTION_TYPES.map((t) => (
                <option key={t} value={t}>
                  {vi.questions.types[t]}
                </option>
              ))}
            </Select>
          </Field>

          <Field label={vi.ai.difficulty} htmlFor="ai-difficulty">
            <Select id="ai-difficulty" {...register("difficulty")}>
              {DIFFICULTIES.map((d) => (
                <option key={d} value={d}>
                  {vi.questions.difficulties[d]}
                </option>
              ))}
            </Select>
          </Field>

          <Field label={vi.ai.count} htmlFor="ai-count" hint={vi.ai.countHint(maxQuestions)} error={errors.count?.message}>
            <Input id="ai-count" type="number" min={1} max={maxQuestions} step={1} {...register("count")} />
          </Field>
        </div>

        <Field label={vi.ai.note} htmlFor="ai-note" error={errors.note?.message}>
          <Textarea
            id="ai-note"
            rows={4}
            maxLength={MAX_NOTE_LENGTH}
            aria-describedby={`${warningId} ${counterId}`}
            {...register("note")}
          />
          <p id={counterId} className="mt-1 text-right text-xs text-muted">
            {vi.ai.noteCounter(note.length)}
          </p>
          <Notice tone="warn" className="mt-2">
            <p id={warningId}>{vi.ai.noteWarning}</p>
          </Notice>
        </Field>

        {blocked && <Notice tone="warn" className="mb-3.5">{blocked}</Notice>}
        {formError && <Notice tone="error" className="mb-3.5">{formError}</Notice>}

        <Button type="submit" disabled={Boolean(blocked) || mutation.isPending}>
          {mutation.isPending ? vi.ai.submitting : vi.ai.submit}
        </Button>
      </form>
    </Panel>
  );
}
