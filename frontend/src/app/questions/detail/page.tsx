"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useRouter, useSearchParams } from "next/navigation";
import { Suspense, useId, useState } from "react";

import { MathMarkdown } from "@/components/math-markdown";
import { PageHeader } from "@/components/shell/page-header";
import { Button } from "@/components/ui/button";
import { EmptyState } from "@/components/ui/empty-state";
import { Field, Input, Radio, Select, Textarea } from "@/components/ui/field";
import { Notice } from "@/components/ui/notice";
import { Panel } from "@/components/ui/panel";
import { StatusText } from "@/components/ui/status-text";
import { Tag, type TagTone } from "@/components/ui/tag";
import { fetchAllSubjects, fetchAllTopics } from "@/lib/api/catalog";
import { api } from "@/lib/api/client";
import { getApiErrorCode, getApiErrorMessage, getUiErrorMessage } from "@/lib/api/error";
import type { components } from "@/lib/api/schema";
import { useAuth } from "@/lib/auth/auth-context";
import { formatDecimalForInput, parseExactDecimal } from "@/lib/format/decimal";
import { questionTone } from "@/lib/ui/status";
import { vi } from "@/messages/vi";

type Question = components["schemas"]["Question"];
type QuestionType = components["schemas"]["QuestionType"];
type Difficulty = components["schemas"]["Difficulty"];
type Grade = components["schemas"]["Grade"];
type QuestionInput = components["schemas"]["QuestionInput"];
type QuestionPayload = components["schemas"]["QuestionPayload"];
type AiVerification = components["schemas"]["AiVerification"];

const TYPES: QuestionType[] = ["MultipleChoice", "TrueFalse", "ShortAnswer", "Essay"];
const DIFFICULTIES: Difficulty[] = ["Recognition", "Understanding", "Application", "HighApplication"];
const GRADES: Grade[] = [10, 11, 12];
const MC_LABELS = ["A", "B", "C", "D"] as const;
const TF_LABELS = ["a", "b", "c", "d"] as const;
const AI_FLAG_TONE: Record<AiVerification, TagTone> = { NotChecked: "neutral", Agree: "ok", Disagree: "rej" };
const dateFormat = new Intl.DateTimeFormat("vi-VN", {
  timeZone: "Asia/Ho_Chi_Minh",
  dateStyle: "short",
  timeStyle: "short",
});

function initMcOptions(q?: Question): Record<(typeof MC_LABELS)[number], string> {
  const map: Record<(typeof MC_LABELS)[number], string> = { A: "", B: "", C: "", D: "" };
  if (q?.type === "MultipleChoice" && "options" in q.payload) {
    for (const opt of q.payload.options) map[opt.label] = opt.text;
  }
  return map;
}

function initEssayRubric(q?: Question): { criterion: string; points: string }[] {
  if (q?.type === "Essay" && "rubric" in q.payload && q.payload.rubric.length > 0) {
    return q.payload.rubric.map((r) => ({ criterion: r.criterion, points: formatDecimalForInput(r.points) }));
  }
  return [{ criterion: "", points: "1" }];
}

function QuestionEditorForm({
  initialQuestion,
  feedback,
  errorMsg,
  loadError,
  onRetry,
  onFeedback,
  onError,
}: {
  initialQuestion?: Question;
  feedback: string | null;
  errorMsg: string | null;
  /** A failed background reload while an older copy is still shown. */
  loadError: string | null;
  onRetry: () => void;
  onFeedback: (message: string) => void;
  onError: (message: string) => void;
}) {
  const uid = useId();
  const q = initialQuestion;
  const id = q?.id ?? null;
  const router = useRouter();
  const queryClient = useQueryClient();
  const { me } = useAuth();

  const [subjectId, setSubjectId] = useState(q?.subjectId ?? "");
  const [topicId, setTopicId] = useState(q?.topicId ?? "");
  const [grade, setGrade] = useState<Grade>(q?.grade ?? 10);
  const [type, setType] = useState<QuestionType>(q?.type ?? "MultipleChoice");
  const [difficulty, setDifficulty] = useState<Difficulty>(q?.difficulty ?? "Recognition");
  const [stem, setStem] = useState(q?.stem ?? "");
  const [explanation, setExplanation] = useState(q?.explanation ?? "");

  const [mcOptions, setMcOptions] = useState(() => initMcOptions(q));
  const [mcCorrect, setMcCorrect] = useState<(typeof MC_LABELS)[number]>(
    q?.type === "MultipleChoice" && "correctLabel" in q.payload ? q.payload.correctLabel : "A",
  );
  const [tfStatements, setTfStatements] = useState(() =>
    q?.type === "TrueFalse" && "statements" in q.payload
      ? q.payload.statements.map((s) => ({ ...s }))
      : TF_LABELS.map((label) => ({ label, text: "", isTrue: true })),
  );
  const [shortAnswerType, setShortAnswerType] = useState<"number" | "text">(
    q?.type === "ShortAnswer" && "answerType" in q.payload ? q.payload.answerType : "number",
  );
  const [shortAnswersText, setShortAnswersText] = useState(
    q?.type === "ShortAnswer" && "acceptedAnswers" in q.payload ? q.payload.acceptedAnswers.join("\n") : "",
  );
  const [shortUnit, setShortUnit] = useState(
    q?.type === "ShortAnswer" && "unit" in q.payload ? (q.payload.unit ?? "") : "",
  );
  const [shortTolerance, setShortTolerance] = useState(
    q?.type === "ShortAnswer" && "tolerance" in q.payload && q.payload.tolerance != null
      ? formatDecimalForInput(q.payload.tolerance)
      : "",
  );
  const [essayAnswer, setEssayAnswer] = useState(
    q?.type === "Essay" && "modelAnswer" in q.payload ? q.payload.modelAnswer : "",
  );
  const [essayRubric, setEssayRubric] = useState(() => initEssayRubric(q));

  const [isDirty, setIsDirty] = useState(false);

  const subjectDepartmentId = (q ? q.departmentId : me?.departmentId) ?? undefined;

  const subjectsQuery = useQuery({
    queryKey: ["catalog", "subjects", subjectDepartmentId ?? "all"],
    enabled: Boolean(subjectDepartmentId) || (!q && me != null),
    queryFn: () => fetchAllSubjects(subjectDepartmentId),
  });

  const topicsQuery = useQuery({
    queryKey: ["catalog", "topics", subjectId, grade],
    enabled: Boolean(subjectId),
    queryFn: () => fetchAllTopics(subjectId, grade),
  });

  const buildPayload = (): QuestionPayload => {
    if (type === "MultipleChoice") {
      return {
        options: MC_LABELS.map((label) => ({ label, text: mcOptions[label] })),
        correctLabel: mcCorrect,
      };
    }
    if (type === "TrueFalse") {
      return { statements: tfStatements };
    }
    if (type === "ShortAnswer") {
      const toleranceTrimmed = shortTolerance.trim();
      let tolerance: number | null = null;
      if (shortAnswerType === "number" && toleranceTrimmed !== "") {
        const parsedTol = parseExactDecimal(toleranceTrimmed);
        if (parsedTol === null || parsedTol < 0) {
          throw new Error(vi.questions.invalidTolerance);
        }
        tolerance = parsedTol;
      }
      return {
        answerType: shortAnswerType,
        acceptedAnswers: shortAnswersText.split("\n").map((s) => s.trim()).filter(Boolean),
        unit: shortUnit.trim() || null,
        tolerance,
      };
    }
    return {
      modelAnswer: essayAnswer,
      rubric: essayRubric.map((r) => {
        const criterion = r.criterion.trim();
        if (!criterion) {
          throw new Error(vi.questions.criterionRequired);
        }
        const pts = parseExactDecimal(r.points);
        if (pts === null) {
          throw new Error(vi.questions.criterionPointsInvalid);
        }
        if (pts <= 0) {
          throw new Error(vi.questions.criterionPointsPositive);
        }
        return {
          criterion,
          points: pts,
        };
      }),
    };
  };

  const persistQuestion = async (): Promise<Question> => {
    const body: QuestionInput = {
      subjectId,
      topicId,
      grade,
      type,
      difficulty,
      stem,
      explanation: explanation.trim() || null,
      payload: buildPayload(),
    };
    if (id) {
      const { data, error } = await api.PUT("/questions/{id}", { params: { path: { id } }, body });
      if (error) throw new Error(getApiErrorMessage(error));
      return data;
    }
    const { data, error } = await api.POST("/questions", { body });
    if (error) throw new Error(getApiErrorMessage(error));
    return data;
  };

  const saveMutation = useMutation({
    mutationFn: async () => {
      const hadAiReview = Boolean(q?.source === "AI" && q.aiReviewedByAuthor);
      const saved = await persistQuestion();
      return { saved, aiReviewWasReset: hadAiReview && !saved.aiReviewedByAuthor };
    },
    onSuccess: ({ saved, aiReviewWasReset }) => {
      setIsDirty(false);
      // Update question cache synchronously so QuestionEditorForm remounts immediately when the save settles,
      // not 200ms later after a background refetch while the user has already started typing again.
      queryClient.setQueryData(["question", saved.id], saved);
      onFeedback(aiReviewWasReset ? vi.questions.aiReviewResetNotice : vi.questions.savedSuccess);
      void queryClient.invalidateQueries({ queryKey: ["questions"] });
      if (!id) router.replace(`/questions/detail/?id=${saved.id}`);
    },
    onError: (err: unknown) => onError(getUiErrorMessage(err)),
  });

  const actionMutation = useMutation({
    mutationFn: async (action: "submit" | "withdraw" | "mark-reviewed" | "archive" | "delete") => {
      if (!id) return null;
      if (action === "delete") {
        const { error } = await api.DELETE("/questions/{id}", { params: { path: { id } } });
        if (error) throw new Error(getApiErrorMessage(error));
        return "deleted";
      }
      // Save unsaved edits before submitting or marking reviewed so reviewers never see stale content and edits are not lost.
      // Defer cache update until the mutation settles so QuestionEditorForm does not remount while the POST is in flight.
      let savedBeforeAction: Question | null = null;
      if ((action === "submit" || action === "mark-reviewed") && isDirty) {
        savedBeforeAction = await persistQuestion();
        setIsDirty(false);
        void queryClient.invalidateQueries({ queryKey: ["questions"] });
        // Editing an AI question resets aiReviewedByAuthor on the server; stop before POST /submit and prompt the author to re-review.
        if (action === "submit" && savedBeforeAction.source === "AI" && !savedBeforeAction.aiReviewedByAuthor) {
          queryClient.setQueryData(["question", savedBeforeAction.id], savedBeforeAction);
          throw new Error(vi.questions.aiReviewResetNotice);
        }
      }
      const pathMap = {
        submit: "/questions/{id}/submit",
        withdraw: "/questions/{id}/withdraw",
        "mark-reviewed": "/questions/{id}/mark-reviewed",
        archive: "/questions/{id}/archive",
      } as const;
      const { data: transitioned, error } = await api.POST(pathMap[action], { params: { path: { id } } });
      if (error) {
        if (savedBeforeAction) {
          queryClient.setQueryData(["question", savedBeforeAction.id], savedBeforeAction);
        }
        if (action === "submit" && (getApiErrorCode(error) === "AiReviewRequired" || q?.source === "AI")) {
          void queryClient.invalidateQueries({ queryKey: ["question", id] });
          if (getApiErrorCode(error) === "AiReviewRequired") {
            throw new Error(savedBeforeAction ? vi.questions.aiReviewResetNotice : vi.questions.aiReviewRequiredNotice);
          }
        }
        throw new Error(getApiErrorMessage(error));
      }
      if (transitioned) {
        queryClient.setQueryData(["question", id], transitioned);
      } else if (savedBeforeAction) {
        queryClient.setQueryData(["question", savedBeforeAction.id], savedBeforeAction);
      }
      return action;
    },
    onSuccess: (result) => {
      void queryClient.invalidateQueries({ queryKey: ["questions"] });
      if (result === "deleted") {
        router.replace("/questions/?deleted=1");
        return;
      }
      onFeedback(vi.questions.transitionSuccess);
      void queryClient.invalidateQueries({ queryKey: ["question", id] });
    },
    onError: (err: unknown) => onError(getUiErrorMessage(err)),
  });

  const isContentAuthor = me?.role === "teacher" || me?.role === "head";
  const isOwner = isContentAuthor && (!q || me?.id === q.createdBy.id);
  const isEditable = isOwner && (!q || q.status === "Draft" || q.status === "Rejected" || q.status === "Approved");
  const canArchive =
    Boolean(q && q.status === "Approved") &&
    (isOwner || me?.role === "admin" || (me?.role === "head" && me.departmentId === q?.departmentId));

  const busy = actionMutation.isPending || saveMutation.isPending;
  const aiReviewPending = Boolean(q && q.source === "AI" && !q.aiReviewedByAuthor);
  const canSubmit = Boolean(q && isOwner && (q.status === "Draft" || q.status === "Rejected"));
  const canMarkReviewed = Boolean(q && isOwner && aiReviewPending);

  const inputId = (name: string) => `${uid}-${name}`;
  const markDirty = () => setIsDirty(true);

  return (
    <>
      <PageHeader
        crumb={{ href: "/questions/", label: vi.questions.title }}
        title={q ? vi.questions.detailTitle : vi.questions.createTitle}
        description={q ? vi.questions.detailSubtitle(vi.questions.types[q.type], q.revision) : undefined}
        actions={
          <>
            {canSubmit && (
              <Button
                variant="boardPrimary"
                disabled={busy || aiReviewPending}
                disabledReason={aiReviewPending && !busy ? vi.questions.aiReviewRequiredNotice : undefined}
                onClick={() => actionMutation.mutate("submit")}
              >
                {vi.questions.submit}
              </Button>
            )}
            {q && isOwner && q.status === "Submitted" && (
              <Button variant="boardSecondary" disabled={busy} onClick={() => actionMutation.mutate("withdraw")}>
                {vi.questions.withdraw}
              </Button>
            )}
            {q && canArchive && (
              <Button
                variant="boardSecondary"
                disabled={busy}
                onClick={() => {
                  if (isDirty && !window.confirm(vi.questions.confirmDiscardBeforeArchive)) return;
                  actionMutation.mutate("archive");
                }}
              >
                {vi.questions.archive}
              </Button>
            )}
          </>
        }
      />
      <div className="px-4 py-5 sm:px-7 sm:py-6">
        <div className={q ? "grid gap-5 lg:grid-cols-[minmax(0,1fr)_320px]" : "grid gap-5"}>
          <div className="min-w-0 space-y-4">
            {q?.reviewNote &&
              (q.status === "Rejected" ? (
                <Notice tone="error">
                  <strong className="font-semibold">{vi.questions.rejectedNotice}</strong> {q.reviewNote}
                </Notice>
              ) : (
                <Notice tone="info">
                  <strong className="font-semibold">{vi.questions.reviewNote}:</strong> {q.reviewNote}
                </Notice>
              ))}
            {loadError && (
              <Notice
                tone="error"
                action={
                  <Button variant="secondary" size="sm" onClick={onRetry}>
                    {vi.auth.retry}
                  </Button>
                }
              >
                {loadError}
              </Notice>
            )}
            {errorMsg && <Notice tone="error">{errorMsg}</Notice>}
            {feedback && <Notice tone="success">{feedback}</Notice>}

            <form
              onSubmit={(e) => {
                e.preventDefault();
                saveMutation.mutate();
              }}
              className="space-y-4"
            >
              <Panel>
                <fieldset disabled={!isEditable || busy} className="min-w-0">
                  <div className="grid grid-cols-1 gap-x-4 sm:grid-cols-3">
                    <Field label={vi.questions.subject} htmlFor={inputId("subject")}>
                      <Select
                        id={inputId("subject")}
                        required
                        value={subjectId}
                        onChange={(e) => {
                          setSubjectId(e.target.value);
                          setTopicId("");
                          markDirty();
                        }}
                      >
                        <option value="">--</option>
                        {subjectsQuery.data?.map((s) => (
                          <option key={s.id} value={s.id}>{s.name}</option>
                        ))}
                      </Select>
                    </Field>

                    <Field label={vi.questions.grade} htmlFor={inputId("grade")}>
                      <Select
                        id={inputId("grade")}
                        value={grade}
                        onChange={(e) => {
                          setGrade(Number(e.target.value) as Grade);
                          setTopicId("");
                          markDirty();
                        }}
                      >
                        {GRADES.map((g) => (
                          <option key={g} value={g}>{vi.common.gradePrefix} {g}</option>
                        ))}
                      </Select>
                    </Field>

                    <Field label={vi.questions.topic} htmlFor={inputId("topic")}>
                      <Select
                        id={inputId("topic")}
                        required
                        value={topicId}
                        onChange={(e) => {
                          setTopicId(e.target.value);
                          markDirty();
                        }}
                      >
                        <option value="">--</option>
                        {topicsQuery.data?.map((t) => (
                          <option key={t.id} value={t.id}>{t.name}</option>
                        ))}
                      </Select>
                    </Field>
                  </div>

                  <div className="grid grid-cols-1 gap-x-4 sm:grid-cols-2">
                    <Field label={vi.questions.type} htmlFor={inputId("type")}>
                      <Select
                        id={inputId("type")}
                        value={type}
                        onChange={(e) => {
                          setType(e.target.value as QuestionType);
                          markDirty();
                        }}
                      >
                        {TYPES.map((t) => (
                          <option key={t} value={t}>{vi.questions.types[t]}</option>
                        ))}
                      </Select>
                    </Field>

                    <Field label={vi.questions.difficulty} htmlFor={inputId("difficulty")}>
                      <Select
                        id={inputId("difficulty")}
                        value={difficulty}
                        onChange={(e) => {
                          setDifficulty(e.target.value as Difficulty);
                          markDirty();
                        }}
                      >
                        {DIFFICULTIES.map((d) => (
                          <option key={d} value={d}>{vi.questions.difficulties[d]}</option>
                        ))}
                      </Select>
                    </Field>
                  </div>

                  <Field label={vi.questions.stem} htmlFor={inputId("stem")}>
                    <Textarea
                      id={inputId("stem")}
                      required
                      rows={4}
                      value={stem}
                      onChange={(e) => {
                        setStem(e.target.value);
                        markDirty();
                      }}
                      className="font-mono"
                    />
                  </Field>

                  {stem.trim() && (
                    <div className="mb-3.5 rounded-md border border-line bg-paper p-3 text-[13.5px]">
                      <p className="mb-1 text-[12.5px] font-semibold text-muted">{vi.questions.preview}</p>
                      <MathMarkdown>{stem}</MathMarkdown>
                    </div>
                  )}

                  {type === "MultipleChoice" && (
                    <div className="mb-3.5">
                      <p className="mb-[5px] text-[12.5px] font-semibold text-ink-soft">{vi.questions.options}</p>
                      <div className="space-y-2">
                        {MC_LABELS.map((label) => (
                          <div key={label} className="flex items-center gap-3">
                            <Radio
                              name="correctOption"
                              label={<span className="inline-block w-5 font-semibold">{label}.</span>}
                              checked={mcCorrect === label}
                              onChange={() => {
                                setMcCorrect(label);
                                markDirty();
                              }}
                              aria-label={`${vi.questions.correctOption} ${label}`}
                            />
                            <Input
                              required
                              aria-label={`${vi.questions.options} ${label}`}
                              value={mcOptions[label]}
                              onChange={(e) => {
                                setMcOptions((prev) => ({ ...prev, [label]: e.target.value }));
                                markDirty();
                              }}
                              className="flex-1"
                            />
                          </div>
                        ))}
                      </div>
                    </div>
                  )}

                  {type === "TrueFalse" && (
                    <div className="mb-3.5">
                      <p className="mb-[5px] text-[12.5px] font-semibold text-ink-soft">{vi.questions.trueFalseStatements}</p>
                      <div className="space-y-2">
                        {tfStatements.map((stmt, idx) => (
                          <div key={stmt.label} className="flex items-center gap-3">
                            <span className="w-5 font-semibold">{stmt.label})</span>
                            <Input
                              required
                              aria-label={`${vi.questions.trueFalseStatements} ${stmt.label}`}
                              value={stmt.text}
                              onChange={(e) => {
                                setTfStatements((prev) => prev.map((item, i) => (i === idx ? { ...item, text: e.target.value } : item)));
                                markDirty();
                              }}
                              className="flex-1"
                            />
                            <div className="w-24 shrink-0">
                              <Select
                                aria-label={`${vi.questions.trueLabel} / ${vi.questions.falseLabel} ${stmt.label}`}
                                value={stmt.isTrue ? "true" : "false"}
                                onChange={(e) => {
                                  setTfStatements((prev) =>
                                    prev.map((item, i) => (i === idx ? { ...item, isTrue: e.target.value === "true" } : item))
                                  );
                                  markDirty();
                                }}
                              >
                                <option value="true">{vi.questions.trueLabel}</option>
                                <option value="false">{vi.questions.falseLabel}</option>
                              </Select>
                            </div>
                          </div>
                        ))}
                      </div>
                    </div>
                  )}

                  {type === "ShortAnswer" && (
                    <div className="grid grid-cols-1 gap-x-4 sm:grid-cols-2">
                      <Field label={vi.questions.shortAnswerValue} htmlFor={inputId("short-answers")}>
                        <Textarea
                          id={inputId("short-answers")}
                          required
                          rows={2}
                          value={shortAnswersText}
                          onChange={(e) => {
                            setShortAnswersText(e.target.value);
                            markDirty();
                          }}
                        />
                      </Field>
                      <div>
                        <Field label={vi.questions.shortAnswerTypeLabel} htmlFor={inputId("short-type")}>
                          <Select
                            id={inputId("short-type")}
                            value={shortAnswerType}
                            onChange={(e) => {
                              setShortAnswerType(e.target.value as "number" | "text");
                              markDirty();
                            }}
                          >
                            <option value="number">{vi.questions.shortAnswerTypeNumber}</option>
                            <option value="text">{vi.questions.shortAnswerTypeText}</option>
                          </Select>
                        </Field>
                        <div className="mb-3.5 space-y-2">
                          <Input
                            aria-label={vi.questions.shortUnitPlaceholder}
                            placeholder={vi.questions.shortUnitPlaceholder}
                            value={shortUnit}
                            onChange={(e) => {
                              setShortUnit(e.target.value);
                              markDirty();
                            }}
                          />
                          {shortAnswerType === "number" && (
                            <Input
                              type="text"
                              inputMode="decimal"
                              aria-label={vi.questions.shortTolerance}
                              placeholder={vi.questions.shortTolerance}
                              value={shortTolerance}
                              onChange={(e) => {
                                setShortTolerance(e.target.value);
                                markDirty();
                              }}
                            />
                          )}
                        </div>
                      </div>
                    </div>
                  )}

                  {type === "Essay" && (
                    <>
                      <Field label={vi.questions.modelAnswer} htmlFor={inputId("model-answer")}>
                        <Textarea
                          id={inputId("model-answer")}
                          required
                          rows={3}
                          value={essayAnswer}
                          onChange={(e) => {
                            setEssayAnswer(e.target.value);
                            markDirty();
                          }}
                        />
                      </Field>
                      <div className="mb-3.5">
                        <p className="mb-[5px] text-[12.5px] font-semibold text-ink-soft">{vi.questions.essayRubric}</p>
                        <div className="space-y-2">
                          {essayRubric.map((row, idx) => (
                            <div key={idx} className="flex items-center gap-2">
                              <Input
                                required
                                aria-label={vi.questions.criterionPlaceholder}
                                placeholder={vi.questions.criterionPlaceholder}
                                value={row.criterion}
                                onChange={(e) => {
                                  setEssayRubric((prev) => prev.map((item, i) => (i === idx ? { ...item, criterion: e.target.value } : item)));
                                  markDirty();
                                }}
                                className="flex-1"
                              />
                              <div className="w-28 shrink-0">
                                <Input
                                  required
                                  type="text"
                                  inputMode="decimal"
                                  aria-label={vi.review.points}
                                  value={row.points}
                                  onChange={(e) => {
                                    setEssayRubric((prev) => prev.map((item, i) => (i === idx ? { ...item, points: e.target.value } : item)));
                                    markDirty();
                                  }}
                                />
                              </div>
                              {essayRubric.length > 1 && (
                                <Button
                                  variant="danger"
                                  size="sm"
                                  onClick={() => {
                                    setEssayRubric((prev) => prev.filter((_, i) => i !== idx));
                                    markDirty();
                                  }}
                                >
                                  {vi.questions.removeCriterion}
                                </Button>
                              )}
                            </div>
                          ))}
                        </div>
                        <Button
                          variant="secondary"
                          size="sm"
                          className="mt-2"
                          onClick={() => {
                            setEssayRubric((prev) => [...prev, { criterion: "", points: "1" }]);
                            markDirty();
                          }}
                        >
                          + {vi.questions.addCriterion}
                        </Button>
                      </div>
                    </>
                  )}

                  <Field label={vi.questions.explanation} htmlFor={inputId("explanation")} className="mb-0">
                    <Textarea
                      id={inputId("explanation")}
                      rows={2}
                      value={explanation}
                      onChange={(e) => {
                        setExplanation(e.target.value);
                        markDirty();
                      }}
                    />
                  </Field>
                </fieldset>
              </Panel>

              <div className="flex flex-wrap items-center justify-between gap-3">
                <div>
                  {q && isOwner && (q.status === "Draft" || q.status === "Rejected") && (
                    <Button
                      variant="danger"
                      disabled={busy}
                      onClick={() => {
                        if (window.confirm(vi.common.confirmDelete)) actionMutation.mutate("delete");
                      }}
                    >
                      {vi.common.delete}
                    </Button>
                  )}
                </div>
                {isOwner && isEditable && (
                  <Button type="submit" disabled={busy}>
                    {saveMutation.isPending ? vi.common.saving : vi.common.save}
                  </Button>
                )}
              </div>
            </form>
          </div>

          {q && (
            <aside className="min-w-0 space-y-4">
              <Panel title={vi.questions.infoPanel}>
                <dl className="grid grid-cols-[auto_minmax(0,1fr)] gap-x-4 gap-y-2 text-[13px]">
                  <dt className="text-muted">{vi.common.status}</dt>
                  <dd>
                    <StatusText tone={questionTone(q.status)}>{vi.questions.statuses[q.status]}</StatusText>
                  </dd>
                  <dt className="text-muted">{vi.questions.source}</dt>
                  <dd>
                    <Tag tone={q.source === "AI" ? "ai" : "neutral"}>
                      {q.source === "AI" ? vi.questions.aiSource : vi.questions.manualSource}
                    </Tag>
                  </dd>
                  <dt className="text-muted">{vi.questions.revision}</dt>
                  <dd className="tabular-nums">{q.revision}</dd>
                  <dt className="text-muted">{vi.questions.createdBy}</dt>
                  <dd className="min-w-0 break-words">{q.createdBy.fullName}</dd>
                  <dt className="text-muted">{vi.questions.updatedAt}</dt>
                  <dd className="tabular-nums">{dateFormat.format(new Date(q.updatedAt))}</dd>
                </dl>
              </Panel>

              <Panel title={vi.questions.aiVerification}>
                {q.source === "AI" ? (
                  <>
                    <p className="mb-1.5">
                      <Tag tone={AI_FLAG_TONE[q.aiVerification]}>{vi.questions.aiFlags[q.aiVerification]}</Tag>
                    </p>
                    {q.aiVerificationNote && <p className="text-[12.5px] text-ink-soft">{q.aiVerificationNote}</p>}
                  </>
                ) : (
                  <p className="text-[12.5px] text-ink-soft">{vi.questions.notAiGenerated}</p>
                )}
              </Panel>

              <Panel title={vi.questions.authorReviewPanel}>
                {q.source === "AI" ? (
                  <>
                    <p className="mb-2 text-[13px] font-semibold">
                      {q.aiReviewedByAuthor ? vi.questions.aiReviewed : vi.questions.aiNotReviewed}
                    </p>
                    <p className="mb-2.5 text-[12.5px] text-ink-soft">{vi.questions.authorReviewHint}</p>
                    {canMarkReviewed && (
                      <Button variant="secondary" size="sm" disabled={busy} onClick={() => actionMutation.mutate("mark-reviewed")}>
                        {vi.questions.markReviewed}
                      </Button>
                    )}
                  </>
                ) : (
                  <p className="text-[12.5px] text-ink-soft">{vi.questions.authorReviewNotApplicable}</p>
                )}
              </Panel>
            </aside>
          )}
        </div>
      </div>
    </>
  );
}

function QuestionDetailInner() {
  const id = useSearchParams().get("id");
  const { me } = useAuth();
  const [feedback, setFeedback] = useState<string | null>(null);
  const [errorMsg, setErrorMsg] = useState<string | null>(null);

  const canCreate = me?.role === "teacher" || me?.role === "head";

  const questionQuery = useQuery({
    queryKey: ["question", id],
    enabled: Boolean(id),
    queryFn: async () => {
      const { data, error } = await api.GET("/questions/{id}", { params: { path: { id: id! } } });
      if (error) throw new Error(getApiErrorMessage(error));
      return data;
    },
  });

  const header = (
    <PageHeader
      crumb={{ href: "/questions/", label: vi.questions.title }}
      title={id ? vi.questions.detailTitle : vi.questions.createTitle}
    />
  );

  if (!id && !canCreate) {
    return (
      <>
        {header}
        <div className="px-4 py-5 sm:px-7 sm:py-6">
          <Notice tone="error">{vi.common.forbidden}</Notice>
        </div>
      </>
    );
  }

  if (id && !questionQuery.data) {
    return (
      <>
        {header}
        <div className="px-4 py-5 sm:px-7 sm:py-6">
          {questionQuery.isLoading && <EmptyState busy title={vi.common.loading} />}
          {questionQuery.isError && (
            <Notice
              tone="error"
              action={
                <Button variant="secondary" size="sm" onClick={() => void questionQuery.refetch()}>
                  {vi.auth.retry}
                </Button>
              }
            >
              {getUiErrorMessage(questionQuery.error)}
            </Notice>
          )}
        </div>
      </>
    );
  }

  return (
    <QuestionEditorForm
      key={questionQuery.data ? `${questionQuery.data.id}-${questionQuery.data.updatedAt}` : "new"}
      initialQuestion={questionQuery.data}
      feedback={feedback}
      errorMsg={errorMsg}
      loadError={id && questionQuery.isError ? getUiErrorMessage(questionQuery.error) : null}
      onRetry={() => void questionQuery.refetch()}
      onFeedback={(msg) => {
        setErrorMsg(null);
        setFeedback(msg);
      }}
      onError={(msg) => {
        setFeedback(null);
        setErrorMsg(msg);
      }}
    />
  );
}

export default function QuestionDetailPage() {
  return (
    <Suspense fallback={<EmptyState busy title={vi.common.loading} />}>
      <QuestionDetailInner />
    </Suspense>
  );
}
