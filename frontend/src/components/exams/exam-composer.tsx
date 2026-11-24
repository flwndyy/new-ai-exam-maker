"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useEffect, useMemo, useRef, useState } from "react";
import { useFieldArray, useForm, useWatch } from "react-hook-form";

import { QuestionPicker } from "@/components/exams/question-picker";
import { VersionItemCard } from "@/components/exams/version-item-card";
import { api } from "@/lib/api/client";
import { getApiErrorMessage } from "@/lib/api/error";
import type { components } from "@/lib/api/schema";
import {
  type ComposerFormInput,
  composerSchema,
  DEFAULT_ITEM_POINTS,
  itemViewFromQuestion,
  MAX_EXAM_ITEMS,
  toFormValues,
} from "@/lib/exams/composer-schema";
import { splitPointsEvenly, sumDecimalStrings } from "@/lib/exams/points";
import { useUnsavedGuard } from "@/lib/exams/use-unsaved-guard";
import { vi } from "@/messages/vi";

type ExamDetail = components["schemas"]["ExamDetail"];
type ExamVersion = components["schemas"]["ExamVersion"];
type ExamVersionUpdate = components["schemas"]["ExamVersionUpdate"];

const SPLIT_TOTAL = 10;
// API field keys like "items[2].points" map to RHF paths like "items.2.points".
const ITEM_POINTS_KEY = /^items\[(\d+)\]\.points$/;

type SaveFailure = { status: number; error: unknown };

/** Editable Draft/Rejected version: ordered items with points on the left, bank picker on the right. */
export function ExamComposer({
  exam,
  version,
  meId,
  highlightIds,
  onDirtyChange,
}: {
  exam: ExamDetail;
  version: ExamVersion;
  meId: string;
  highlightIds: ReadonlySet<string>;
  onDirtyChange: (dirty: boolean) => void;
}) {
  const queryClient = useQueryClient();
  const [banner, setBanner] = useState<{ kind: "error" | "conflict" | "success"; text: string } | null>(null);

  const {
    control,
    register,
    handleSubmit,
    reset,
    setValue,
    setError,
    formState: { errors, isDirty },
  } = useForm<ComposerFormInput, unknown, ExamVersionUpdate>({
    resolver: zodResolver(composerSchema),
    defaultValues: toFormValues(version),
  });
  const { fields, append, move, remove } = useFieldArray({ control, name: "items", keyName: "fieldKey" });

  // Re-sync with the server copy only while there are no local edits; save and reload reset explicitly,
  // so a background refetch can never discard unsaved work. The parent keys this component by version id.
  // `appliedVersion` is the last prop copy loaded into the form; a stale prop never overwrites a fresher save.
  const appliedVersion = useRef(version);
  useEffect(() => {
    if (version === appliedVersion.current || isDirty) return;
    appliedVersion.current = version;
    reset(toFormValues(version));
  }, [version, reset, isDirty]);

  useEffect(() => onDirtyChange(isDirty), [isDirty, onDirtyChange]);
  useUnsavedGuard(isDirty);

  const watchedItems = useWatch({ control, name: "items" });
  const total = sumDecimalStrings((watchedItems ?? []).map((i) => i.points ?? ""));
  const selectedIds = useMemo(() => new Set((watchedItems ?? []).map((i) => i.sourceQuestionId)), [watchedItems]);

  const mutation = useMutation({
    mutationFn: async (body: ExamVersionUpdate) => {
      const { data, error, response } = await api.PUT("/exam-versions/{vid}", { params: { path: { vid: version.id } }, body });
      if (error || !data) throw { status: response.status, error } satisfies SaveFailure;
      return data;
    },
    onSuccess: (data) => {
      reset(toFormValues(data));
      queryClient.setQueryData(["examVersion", version.id], data);
      void queryClient.invalidateQueries({ queryKey: ["exam", exam.id] });
      void queryClient.invalidateQueries({ queryKey: ["exams"] });
      setBanner({ kind: "success", text: vi.exams.savedSuccess });
    },
    onError: (failure: unknown) => {
      const { status, error } = (failure ?? {}) as Partial<SaveFailure>;
      if (status === 409) {
        setBanner({ kind: "conflict", text: vi.exams.conflictReload });
        return;
      }
      const fieldErrors = (error as { errors?: Record<string, string[]> } | undefined)?.errors ?? {};
      let unmatched = Object.keys(fieldErrors).length === 0;
      for (const [key, list] of Object.entries(fieldErrors)) {
        const itemMatch = ITEM_POINTS_KEY.exec(key);
        if (key === "durationMinutes") setError("durationMinutes", { message: list.join(", ") });
        else if (itemMatch) setError(`items.${Number(itemMatch[1])}.points`, { message: list.join(", ") });
        else unmatched = true;
      }
      setBanner(unmatched ? { kind: "error", text: getApiErrorMessage(error) } : null);
    },
  });

  const saving = mutation.isPending;

  const reload = () => {
    setBanner(null);
    appliedVersion.current = version;
    reset(toFormValues(version));
    void queryClient.invalidateQueries({ queryKey: ["examVersion", version.id] });
    void queryClient.invalidateQueries({ queryKey: ["exam", exam.id] });
  };

  const splitEvenly = () => {
    splitPointsEvenly(SPLIT_TOTAL, fields.length).forEach((points, i) =>
      setValue(`items.${i}.points`, points, { shouldDirty: true, shouldValidate: true }),
    );
  };

  const itemsError = errors.items?.root?.message ?? errors.items?.message;
  const bannerClass = {
    error: "border-rose-300 bg-rose-50 text-rose-800",
    conflict: "border-amber-300 bg-amber-50 text-amber-900",
    success: "border-emerald-300 bg-emerald-50 text-emerald-800",
  };

  return (
    <div className="grid grid-cols-1 gap-6 lg:grid-cols-2">
      <form
        noValidate
        aria-label={vi.exams.itemsTitle}
        onSubmit={handleSubmit((body) => {
          setBanner(null);
          mutation.mutate(body);
        })}
        className="min-w-0"
      >
        {/* Lock every edit while a save is in flight: the response resets the form, which would
            otherwise silently drop anything typed after the request was sent. */}
        <fieldset disabled={saving} aria-busy={saving} className="m-0 min-w-0 space-y-3 border-0 p-0">
          <h2 className="text-lg font-semibold text-slate-900">{vi.exams.itemsTitle}</h2>
          <div className="flex flex-wrap items-end gap-4 text-sm">
            <label className="space-y-1">
              <span className="block font-medium text-slate-700">{vi.exams.duration}</span>
              <input {...register("durationMinutes")} inputMode="numeric" className="w-24 rounded border border-slate-300 px-2 py-1" />
            </label>
            <span>
              {vi.exams.totalPoints}: <strong data-testid="total-points">{total}</strong>
            </span>
            <span>
              {vi.exams.questionCount}: <strong>{fields.length}</strong>
            </span>
          </div>
          {errors.durationMinutes && <p className="text-xs text-rose-600">{errors.durationMinutes.message}</p>}
          {itemsError && <p className="text-xs text-rose-600">{itemsError}</p>}

          {fields.length === 0 ? (
            <p className="rounded border border-dashed border-slate-300 p-4 text-sm text-slate-500">{vi.exams.noItems}</p>
          ) : (
            <ol className="space-y-2">
              {fields.map((field, index) => {
                const n = index + 1;
                const pointsError = errors.items?.[index]?.points?.message;
                return (
                  <VersionItemCard key={field.fieldKey} index={index} view={field.view} highlighted={highlightIds.has(field.sourceQuestionId)}>
                    <label className="flex items-center gap-1 text-sm">
                      <span>{vi.exams.pointsLabel}</span>
                      <input
                        {...register(`items.${index}.points`)}
                        aria-label={`${vi.exams.pointsLabel} ${n}`}
                        inputMode="decimal"
                        className="w-20 rounded border border-slate-300 px-2 py-1"
                      />
                    </label>
                    <button
                      type="button"
                      aria-label={`${vi.exams.moveUp} ${n}`}
                      disabled={index === 0}
                      onClick={() => move(index, index - 1)}
                      className="rounded border px-2 py-1 text-sm disabled:opacity-30"
                    >
                      ↑
                    </button>
                    <button
                      type="button"
                      aria-label={`${vi.exams.moveDown} ${n}`}
                      disabled={index === fields.length - 1}
                      onClick={() => move(index, index + 1)}
                      className="rounded border px-2 py-1 text-sm disabled:opacity-30"
                    >
                      ↓
                    </button>
                    <button
                      type="button"
                      aria-label={`${vi.exams.remove} ${n}`}
                      onClick={() => remove(index)}
                      className="rounded border border-rose-200 px-2 py-1 text-sm text-rose-700"
                    >
                      ✕
                    </button>
                    {pointsError && <span className="w-full text-xs text-rose-600">{pointsError}</span>}
                  </VersionItemCard>
                );
              })}
            </ol>
          )}

          {banner && (
            <div className={`flex flex-wrap items-center gap-3 rounded border p-3 text-sm ${bannerClass[banner.kind]}`}>
              <span>{banner.text}</span>
              {banner.kind === "conflict" && (
                <button type="button" onClick={reload} className="rounded border border-current px-3 py-1 font-medium">
                  {vi.exams.reload}
                </button>
              )}
            </div>
          )}

          <div className="flex flex-wrap gap-2">
            <button
              type="button"
              onClick={splitEvenly}
              disabled={fields.length === 0}
              className="rounded border border-slate-300 px-4 py-2 text-sm disabled:opacity-40"
            >
              {vi.exams.splitEvenly}
            </button>
            <button
              type="submit"
              disabled={!isDirty || saving}
              className="rounded bg-slate-900 px-4 py-2 text-sm font-medium text-white hover:bg-slate-800 disabled:opacity-40"
            >
              {saving ? vi.common.saving : vi.exams.save}
            </button>
          </div>
        </fieldset>
      </form>

      <QuestionPicker
        subjectId={exam.subjectId}
        grade={exam.grade}
        meId={meId}
        selectedIds={selectedIds}
        addDisabled={saving}
        onAdd={(q) => {
          if (saving) return;
          if (fields.length >= MAX_EXAM_ITEMS) {
            setBanner({ kind: "error", text: vi.exams.validation.tooMany });
            return;
          }
          append({ sourceQuestionId: q.id, points: DEFAULT_ITEM_POINTS, view: itemViewFromQuestion(q) });
        }}
      />
    </div>
  );
}
