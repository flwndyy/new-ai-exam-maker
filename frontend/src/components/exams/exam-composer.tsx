"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useEffect, useId, useMemo, useRef, useState } from "react";
import { useFieldArray, useForm, useWatch } from "react-hook-form";

import { QuestionPicker } from "@/components/exams/question-picker";
import { VersionItemCard } from "@/components/exams/version-item-card";
import { Button } from "@/components/ui/button";
import { EmptyState } from "@/components/ui/empty-state";
import { Input } from "@/components/ui/field";
import { MetaBar } from "@/components/ui/meta-bar";
import { Notice } from "@/components/ui/notice";
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

/**
 * Editable Draft/Rejected version: ordered items with points on the left, bank picker on the right.
 * By default the Save button sits in the meta bar. When the page passes `externalForm`, it owns the Save
 * button (rendered in the board header, in DOM order) and submits this form by its id; the composer then
 * reports its saving state through `onSavingChange` so the page can disable that button.
 */
export function ExamComposer({
  exam,
  version,
  meId,
  highlightIds,
  onDirtyChange,
  externalForm,
}: {
  exam: ExamDetail;
  version: ExamVersion;
  meId: string;
  highlightIds: ReadonlySet<string>;
  onDirtyChange: (dirty: boolean) => void;
  externalForm?: { formId: string; onSavingChange: (saving: boolean) => void };
}) {
  const queryClient = useQueryClient();
  const ownFormId = useId();
  const formId = externalForm?.formId ?? ownFormId;
  const durationId = useId();
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

  const onSavingChange = externalForm?.onSavingChange;
  useEffect(() => {
    onSavingChange?.(saving);
  }, [saving, onSavingChange]);
  useEffect(() => () => onSavingChange?.(false), [onSavingChange]);

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
  const durationError = errors.durationMinutes?.message;

  // The button sits outside the <form> (meta bar), so it points at the form by id.
  const saveButton = (
    <Button type="submit" form={formId} disabled={!isDirty || saving}>
      {saving ? vi.common.saving : vi.exams.save}
    </Button>
  );

  return (
    <div>
      <MetaBar
        items={[
          {
            label: <label htmlFor={durationId}>{vi.exams.duration}</label>,
            value: (
              <>
                <Input
                  id={durationId}
                  form={formId}
                  {...register("durationMinutes")}
                  disabled={saving}
                  inputMode="numeric"
                  className="min-h-0! w-[74px]! px-2 py-1 text-right tabular-nums"
                />
                {durationError && <p className="mt-1 text-xs font-normal text-rej">{durationError}</p>}
              </>
            ),
          },
          { label: vi.exams.questionCount, value: fields.length },
          { label: vi.exams.totalPoints, value: <span data-testid="total-points">{total}</span> },
        ]}
        actions={
          <>
            <Button variant="secondary" size="sm" onClick={splitEvenly} disabled={fields.length === 0 || saving}>
              {vi.exams.splitEvenly}
            </Button>
            {!externalForm && saveButton}
          </>
        }
      />


      {banner && (
        <Notice
          tone={banner.kind === "conflict" ? "warn" : banner.kind}
          className="mb-4"
          action={
            banner.kind === "conflict" ? (
              <Button variant="secondary" size="sm" onClick={reload}>
                {vi.exams.reload}
              </Button>
            ) : undefined
          }
        >
          {banner.text}
        </Notice>
      )}

      <div className="grid grid-cols-1 gap-6 xl:grid-cols-2">
        <form
          id={formId}
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
          <fieldset disabled={saving} aria-busy={saving} className="m-0 min-w-0 border-0 p-0">
            <h2 className="mb-2.5 flex flex-wrap items-baseline gap-x-2.5 text-[15px] font-semibold">
              {vi.exams.itemsTitle}
              <small className="text-xs font-normal text-muted">{vi.exams.reorderHint}</small>
            </h2>
            {itemsError && <p className="mb-2 text-xs text-rej">{itemsError}</p>}

            {fields.length === 0 ? (
              <EmptyState title={vi.exams.noItems} className="min-h-[120px]" />
            ) : (
              <ol>
                {fields.map((field, index) => {
                  const n = index + 1;
                  const pointsError = errors.items?.[index]?.points?.message;
                  return (
                    <VersionItemCard key={field.fieldKey} index={index} view={field.view} highlighted={highlightIds.has(field.sourceQuestionId)}>
                      <label className="flex items-center gap-1.5 text-xs text-muted">
                        <span>{vi.exams.pointsLabel}</span>
                        <Input
                          {...register(`items.${index}.points`)}
                          aria-label={`${vi.exams.pointsLabel} ${n}`}
                          inputMode="decimal"
                          className="min-h-0! w-[62px]! px-2 py-1 text-right tabular-nums"
                        />
                      </label>
                      <Button
                        variant="secondary"
                        size="sm"
                        aria-label={`${vi.exams.moveUp} ${n}`}
                        disabled={index === 0}
                        onClick={() => move(index, index - 1)}
                      >
                        ↑
                      </Button>
                      <Button
                        variant="secondary"
                        size="sm"
                        aria-label={`${vi.exams.moveDown} ${n}`}
                        disabled={index === fields.length - 1}
                        onClick={() => move(index, index + 1)}
                      >
                        ↓
                      </Button>
                      <Button variant="danger" size="sm" aria-label={`${vi.exams.remove} ${n}`} onClick={() => remove(index)}>
                        ✕
                      </Button>
                      {pointsError && <span className="w-full text-xs text-rej">{pointsError}</span>}
                    </VersionItemCard>
                  );
                })}
              </ol>
            )}
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
    </div>
  );
}
