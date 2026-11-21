"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { Suspense, useState } from "react";

import { MathMarkdown } from "@/components/math-markdown";
import { api } from "@/lib/api/client";
import { getApiErrorMessage } from "@/lib/api/error";
import type { components } from "@/lib/api/schema";
import { useAuth } from "@/lib/auth/auth-context";
import { vi } from "@/messages/vi";

type Question = components["schemas"]["Question"];
type QuestionType = components["schemas"]["QuestionType"];
type Difficulty = components["schemas"]["Difficulty"];
type Grade = components["schemas"]["Grade"];
type QuestionInput = components["schemas"]["QuestionInput"];
type QuestionPayload = components["schemas"]["QuestionPayload"];

const TYPES: QuestionType[] = ["MultipleChoice", "TrueFalse", "ShortAnswer", "Essay"];
const DIFFICULTIES: Difficulty[] = ["Recognition", "Understanding", "Application", "HighApplication"];
const GRADES: Grade[] = [10, 11, 12];
const MC_LABELS = ["A", "B", "C", "D"] as const;
const TF_LABELS = ["a", "b", "c", "d"] as const;

function initMcOptions(q?: Question): Record<(typeof MC_LABELS)[number], string> {
  const map: Record<(typeof MC_LABELS)[number], string> = { A: "", B: "", C: "", D: "" };
  if (q?.type === "MultipleChoice" && "options" in q.payload) {
    for (const opt of q.payload.options) map[opt.label] = opt.text;
  }
  return map;
}

function QuestionEditorForm({ initialQuestion }: { initialQuestion?: Question }) {
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
  const [essayAnswer, setEssayAnswer] = useState(
    q?.type === "Essay" && "modelAnswer" in q.payload ? q.payload.modelAnswer : "",
  );
  const [essayCriterion, setEssayCriterion] = useState(
    q?.type === "Essay" && "rubric" in q.payload ? (q.payload.rubric[0]?.criterion ?? "") : "",
  );
  const [essayPoints, setEssayPoints] = useState(
    q?.type === "Essay" && "rubric" in q.payload ? String(q.payload.rubric[0]?.points ?? 1) : "1",
  );

  const [feedback, setFeedback] = useState<string | null>(null);
  const [errorMsg, setErrorMsg] = useState<string | null>(null);

  const subjectsQuery = useQuery({
    queryKey: ["catalog", "subjects"],
    queryFn: async () => {
      const { data, error } = await api.GET("/subjects", { params: { query: { pageSize: 100 } } });
      if (error) throw new Error(getApiErrorMessage(error));
      return data.items;
    },
  });

  const topicsQuery = useQuery({
    queryKey: ["catalog", "topics", subjectId, grade],
    enabled: Boolean(subjectId),
    queryFn: async () => {
      const { data, error } = await api.GET("/topics", {
        params: { query: { pageSize: 100, subjectId, grade } },
      });
      if (error) throw new Error(getApiErrorMessage(error));
      return data.items;
    },
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
      return {
        answerType: shortAnswerType,
        acceptedAnswers: shortAnswersText.split("\n").map((s) => s.trim()).filter(Boolean),
        unit: shortUnit.trim() || null,
      };
    }
    return {
      modelAnswer: essayAnswer,
      rubric: [{ criterion: essayCriterion.trim() || "Đáp án đúng", points: Number(essayPoints) || 1 }],
    };
  };

  const saveMutation = useMutation({
    mutationFn: async () => {
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
    },
    onSuccess: (saved) => {
      setErrorMsg(null);
      setFeedback(vi.questions.savedSuccess);
      void queryClient.invalidateQueries({ queryKey: ["questions"] });
      void queryClient.invalidateQueries({ queryKey: ["question", saved.id] });
      if (!id) router.replace(`/questions/detail/?id=${saved.id}`);
    },
    onError: (err: Error) => {
      setFeedback(null);
      setErrorMsg(err.message);
    },
  });

  const actionMutation = useMutation({
    mutationFn: async (action: "submit" | "withdraw" | "mark-reviewed" | "archive" | "delete") => {
      if (!id) return null;
      if (action === "delete") {
        const { error } = await api.DELETE("/questions/{id}", { params: { path: { id } } });
        if (error) throw new Error(getApiErrorMessage(error));
        return "deleted";
      }
      const pathMap = {
        submit: "/questions/{id}/submit",
        withdraw: "/questions/{id}/withdraw",
        "mark-reviewed": "/questions/{id}/mark-reviewed",
        archive: "/questions/{id}/archive",
      } as const;
      const { error } = await api.POST(pathMap[action], { params: { path: { id } } });
      if (error) throw new Error(getApiErrorMessage(error));
      return action;
    },
    onSuccess: (result) => {
      setErrorMsg(null);
      void queryClient.invalidateQueries({ queryKey: ["questions"] });
      if (result === "deleted") {
        router.replace("/questions/");
        return;
      }
      setFeedback(vi.questions.transitionSuccess);
      void queryClient.invalidateQueries({ queryKey: ["question", id] });
    },
    onError: (err: Error) => {
      setFeedback(null);
      setErrorMsg(err.message);
    },
  });

  const isOwner = !q || me?.id === q.createdBy.id;
  const isEditable = !q || q.status === "Draft" || q.status === "Rejected" || q.status === "Approved";

  return (
    <div className="space-y-4">
      {q && (
        <div className="flex flex-wrap items-center gap-2 text-xs">
          <span className="rounded bg-slate-100 px-2.5 py-1 font-medium text-slate-800">
            {vi.questions.statuses[q.status]} · {vi.questions.revision} {q.revision}
          </span>
          {q.source === "AI" && (
            <span className="rounded bg-amber-50 px-2.5 py-1 text-amber-800">
              {vi.questions.aiFlags[q.aiVerification]} ({q.aiReviewedByAuthor ? vi.questions.aiReviewed : vi.questions.aiNotReviewed})
            </span>
          )}
        </div>
      )}

      {q?.reviewNote && (
        <div className="rounded border border-amber-300 bg-amber-50 p-3 text-sm text-amber-900">
          <strong>{vi.questions.reviewNote}:</strong> {q.reviewNote}
        </div>
      )}
      {feedback && <p className="rounded border border-emerald-300 bg-emerald-50 p-3 text-sm text-emerald-800">{feedback}</p>}
      {errorMsg && <p className="rounded border border-rose-300 bg-rose-50 p-3 text-sm text-rose-800">{errorMsg}</p>}

      <form
        onSubmit={(e) => {
          e.preventDefault();
          saveMutation.mutate();
        }}
        className="space-y-4 rounded border border-slate-200 bg-white p-5"
      >
        <div className="grid grid-cols-1 gap-4 sm:grid-cols-3">
          <label className="block text-sm">
            <span className="font-medium text-slate-700">{vi.questions.subject}</span>
            <select
              required
              value={subjectId}
              onChange={(e) => {
                setSubjectId(e.target.value);
                setTopicId("");
              }}
              className="mt-1 w-full rounded border border-slate-300 px-2.5 py-1.5"
            >
              <option value="">--</option>
              {subjectsQuery.data?.map((s) => (
                <option key={s.id} value={s.id}>{s.name}</option>
              ))}
            </select>
          </label>

          <label className="block text-sm">
            <span className="font-medium text-slate-700">{vi.questions.grade}</span>
            <select
              value={grade}
              onChange={(e) => {
                setGrade(Number(e.target.value) as Grade);
                setTopicId("");
              }}
              className="mt-1 w-full rounded border border-slate-300 px-2.5 py-1.5"
            >
              {GRADES.map((g) => (
                <option key={g} value={g}>{vi.common.gradePrefix} {g}</option>
              ))}
            </select>
          </label>

          <label className="block text-sm">
            <span className="font-medium text-slate-700">{vi.questions.topic}</span>
            <select
              required
              value={topicId}
              onChange={(e) => setTopicId(e.target.value)}
              className="mt-1 w-full rounded border border-slate-300 px-2.5 py-1.5"
            >
              <option value="">--</option>
              {topicsQuery.data?.map((t) => (
                <option key={t.id} value={t.id}>{t.name}</option>
              ))}
            </select>
          </label>
        </div>

        <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
          <label className="block text-sm">
            <span className="font-medium text-slate-700">{vi.questions.type}</span>
            <select
              value={type}
              onChange={(e) => setType(e.target.value as QuestionType)}
              className="mt-1 w-full rounded border border-slate-300 px-2.5 py-1.5"
            >
              {TYPES.map((t) => (
                <option key={t} value={t}>{vi.questions.types[t]}</option>
              ))}
            </select>
          </label>

          <label className="block text-sm">
            <span className="font-medium text-slate-700">{vi.questions.difficulty}</span>
            <select
              value={difficulty}
              onChange={(e) => setDifficulty(e.target.value as Difficulty)}
              className="mt-1 w-full rounded border border-slate-300 px-2.5 py-1.5"
            >
              {DIFFICULTIES.map((d) => (
                <option key={d} value={d}>{vi.questions.difficulties[d]}</option>
              ))}
            </select>
          </label>
        </div>

        <label className="block text-sm">
          <span className="font-medium text-slate-700">{vi.questions.stem}</span>
          <textarea
            required
            rows={4}
            value={stem}
            onChange={(e) => setStem(e.target.value)}
            className="mt-1 w-full rounded border border-slate-300 p-2.5 font-mono text-sm"
          />
        </label>

        {stem.trim() && (
          <div className="rounded border border-slate-200 bg-slate-50 p-3 text-sm">
            <p className="mb-1 text-xs font-medium uppercase text-slate-500">{vi.questions.preview}</p>
            <MathMarkdown>{stem}</MathMarkdown>
          </div>
        )}

        {type === "MultipleChoice" && (
          <div className="space-y-2">
            <p className="text-sm font-medium text-slate-700">{vi.questions.options}</p>
            {MC_LABELS.map((label) => (
              <div key={label} className="flex items-center gap-3">
                <input
                  type="radio"
                  name="correctOption"
                  checked={mcCorrect === label}
                  onChange={() => setMcCorrect(label)}
                  aria-label={`${vi.questions.correctOption} ${label}`}
                />
                <span className="w-5 font-semibold">{label}.</span>
                <input
                  required
                  value={mcOptions[label]}
                  onChange={(e) => setMcOptions((prev) => ({ ...prev, [label]: e.target.value }))}
                  className="flex-1 rounded border border-slate-300 px-2.5 py-1 text-sm"
                />
              </div>
            ))}
          </div>
        )}

        {type === "TrueFalse" && (
          <div className="space-y-2">
            <p className="text-sm font-medium text-slate-700">{vi.questions.trueFalseStatements}</p>
            {tfStatements.map((stmt, idx) => (
              <div key={stmt.label} className="flex items-center gap-3">
                <span className="w-5 font-semibold">{stmt.label})</span>
                <input
                  required
                  value={stmt.text}
                  onChange={(e) =>
                    setTfStatements((prev) => prev.map((item, i) => (i === idx ? { ...item, text: e.target.value } : item)))
                  }
                  className="flex-1 rounded border border-slate-300 px-2.5 py-1 text-sm"
                />
                <select
                  value={stmt.isTrue ? "true" : "false"}
                  onChange={(e) =>
                    setTfStatements((prev) =>
                      prev.map((item, i) => (i === idx ? { ...item, isTrue: e.target.value === "true" } : item))
                    )
                  }
                  className="rounded border border-slate-300 px-2 py-1 text-sm"
                >
                  <option value="true">Đúng</option>
                  <option value="false">Sai</option>
                </select>
              </div>
            ))}
          </div>
        )}

        {type === "ShortAnswer" && (
          <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
            <label className="block text-sm">
              <span className="font-medium text-slate-700">{vi.questions.shortAnswerValue}</span>
              <textarea
                required
                rows={2}
                value={shortAnswersText}
                onChange={(e) => setShortAnswersText(e.target.value)}
                className="mt-1 w-full rounded border border-slate-300 p-2 text-sm"
              />
            </label>
            <div className="space-y-2">
              <label className="block text-sm">
                <span className="font-medium text-slate-700">Kiểu đáp án</span>
                <select
                  value={shortAnswerType}
                  onChange={(e) => setShortAnswerType(e.target.value as "number" | "text")}
                  className="mt-1 w-full rounded border border-slate-300 px-2.5 py-1.5 text-sm"
                >
                  <option value="number">Số (number)</option>
                  <option value="text">Chuỗi (text)</option>
                </select>
              </label>
              <input
                placeholder="Đơn vị (tùy chọn)"
                value={shortUnit}
                onChange={(e) => setShortUnit(e.target.value)}
                className="w-full rounded border border-slate-300 px-2.5 py-1.5 text-sm"
              />
            </div>
          </div>
        )}

        {type === "Essay" && (
          <div className="space-y-3">
            <label className="block text-sm">
              <span className="font-medium text-slate-700">{vi.questions.essayRubric}</span>
              <textarea
                required
                rows={3}
                value={essayAnswer}
                onChange={(e) => setEssayAnswer(e.target.value)}
                className="mt-1 w-full rounded border border-slate-300 p-2 text-sm"
              />
            </label>
            <div className="flex gap-3">
              <input
                required
                placeholder="Tiêu chí chấm"
                value={essayCriterion}
                onChange={(e) => setEssayCriterion(e.target.value)}
                className="flex-1 rounded border border-slate-300 px-2.5 py-1.5 text-sm"
              />
              <input
                required
                type="number"
                step="0.25"
                min="0.25"
                value={essayPoints}
                onChange={(e) => setEssayPoints(e.target.value)}
                className="w-28 rounded border border-slate-300 px-2.5 py-1.5 text-sm"
              />
            </div>
          </div>
        )}

        <label className="block text-sm">
          <span className="font-medium text-slate-700">{vi.questions.explanation}</span>
          <textarea
            rows={2}
            value={explanation}
            onChange={(e) => setExplanation(e.target.value)}
            className="mt-1 w-full rounded border border-slate-300 p-2 text-sm"
          />
        </label>

        <div className="flex flex-wrap items-center gap-3 pt-2">
          {isOwner && isEditable && (
            <button
              type="submit"
              disabled={saveMutation.isPending}
              className="rounded bg-slate-900 px-4 py-2 text-sm font-medium text-white hover:bg-slate-800 disabled:opacity-50"
            >
              {saveMutation.isPending ? vi.common.saving : vi.common.save}
            </button>
          )}

          {q && isOwner && (q.status === "Draft" || q.status === "Rejected") && (
            <button
              type="button"
              onClick={() => actionMutation.mutate("submit")}
              className="rounded bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700"
            >
              {vi.questions.submit}
            </button>
          )}

          {q && isOwner && q.status === "Submitted" && (
            <button
              type="button"
              onClick={() => actionMutation.mutate("withdraw")}
              className="rounded border border-slate-300 px-4 py-2 text-sm hover:bg-slate-50"
            >
              {vi.questions.withdraw}
            </button>
          )}

          {q && isOwner && q.source === "AI" && !q.aiReviewedByAuthor && (
            <button
              type="button"
              onClick={() => actionMutation.mutate("mark-reviewed")}
              className="rounded border border-amber-400 bg-amber-50 px-4 py-2 text-sm text-amber-900 hover:bg-amber-100"
            >
              {vi.questions.markReviewed}
            </button>
          )}

          {q && (q.status === "Approved" || q.status === "Rejected") && (
            <button
              type="button"
              onClick={() => actionMutation.mutate("archive")}
              className="rounded border border-slate-300 px-4 py-2 text-sm hover:bg-slate-50"
            >
              {vi.questions.archive}
            </button>
          )}

          {q && isOwner && (q.status === "Draft" || q.status === "Rejected") && (
            <button
              type="button"
              onClick={() => {
                if (window.confirm(vi.common.confirmDelete)) actionMutation.mutate("delete");
              }}
              className="rounded border border-rose-300 px-4 py-2 text-sm text-rose-700 hover:bg-rose-50"
            >
              {vi.common.delete}
            </button>
          )}
        </div>
      </form>
    </div>
  );
}

function QuestionDetailInner() {
  const id = useSearchParams().get("id");
  const questionQuery = useQuery({
    queryKey: ["question", id],
    enabled: Boolean(id),
    queryFn: async () => {
      const { data, error } = await api.GET("/questions/{id}", { params: { path: { id: id! } } });
      if (error) throw new Error(getApiErrorMessage(error));
      return data;
    },
  });

  return (
    <section className="space-y-6">
      <div>
        <Link href="/questions/" className="text-sm text-slate-600 hover:underline">
          ← {vi.common.back}
        </Link>
        <h1 className="mt-1 text-2xl font-bold text-slate-900">
          {id ? vi.questions.detailTitle : vi.questions.createTitle}
        </h1>
      </div>

      {id && questionQuery.isLoading ? (
        <p className="text-sm text-slate-600">{vi.common.loading}</p>
      ) : (
        <QuestionEditorForm
          key={questionQuery.data ? `${questionQuery.data.id}-${questionQuery.data.updatedAt}` : "new"}
          initialQuestion={questionQuery.data}
        />
      )}
    </section>
  );
}

export default function QuestionDetailPage() {
  return (
    <Suspense fallback={<p>{vi.common.loading}</p>}>
      <QuestionDetailInner />
    </Suspense>
  );
}
