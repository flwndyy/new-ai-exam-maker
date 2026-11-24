import { z } from "zod";

import type { components } from "@/lib/api/schema";
import { formatDecimalForInput, parseExactDecimal } from "@/lib/format/decimal";
import { vi } from "@/messages/vi";

type ExamVersion = components["schemas"]["ExamVersion"];
type ExamVersionItem = components["schemas"]["ExamVersionItem"];
type ExamVersionUpdate = components["schemas"]["ExamVersionUpdate"];
type Question = components["schemas"]["Question"];
type QuestionType = components["schemas"]["QuestionType"];
type Difficulty = components["schemas"]["Difficulty"];
type QuestionPayload = components["schemas"]["QuestionPayload"];
type QuestionStatus = components["schemas"]["QuestionStatus"];

export const MAX_EXAM_ITEMS = 200;
export const DEFAULT_ITEM_POINTS = "1";

/** Display-only data for one composer row; never sent to the API. */
export type ItemView = {
  stem: string | null;
  type: QuestionType | null;
  difficulty: Difficulty | null;
  payload: QuestionPayload | null;
  explanation: string | null;
  sourceRevision: number | null;
  currentSourceRevision: number | null;
  sourceStatus: QuestionStatus | null;
};

export type ComposerItem = { sourceQuestionId: string; points: string; view: ItemView };
export type ComposerFormInput = { durationMinutes: string; items: ComposerItem[] };

const POINTS_RE = /^\d+([.,]\d+)?$/;
const msg = vi.exams.validation;

const itemSchema = z.object({
  sourceQuestionId: z.string().min(1),
  points: z
    .string()
    .refine((p) => {
      if (!POINTS_RE.test(p.trim())) return false;
      const n = parseExactDecimal(p);
      return n !== null && n > 0;
    }, msg.pointsInvalid)
    .transform((p) => parseExactDecimal(p) as number),
  view: z.custom<ItemView>(),
});

/** Form schema for PUT /exam-versions/{vid}; output is the exact request body. */
export const composerSchema = z.object({
  durationMinutes: z
    .string()
    .refine((d) => /^\d+$/.test(d.trim()) && Number(d) >= 1 && Number(d) <= 600, msg.durationInvalid)
    .transform((d) => Number(d)),
  items: z
    .array(itemSchema)
    .max(MAX_EXAM_ITEMS, msg.tooMany)
    .superRefine((items, ctx) => {
      const ids = items.map((i) => i.sourceQuestionId);
      if (new Set(ids).size !== ids.length) ctx.addIssue({ code: "custom", message: msg.duplicate });
    })
    .transform((items) => items.map(({ sourceQuestionId, points }) => ({ sourceQuestionId, points }))),
}) satisfies z.ZodType<ExamVersionUpdate, ComposerFormInput>;

export function itemViewFromVersionItem(item: ExamVersionItem): ItemView {
  return {
    stem: item.stem,
    type: item.type,
    difficulty: item.difficulty,
    payload: item.payload,
    explanation: item.explanation ?? null,
    sourceRevision: item.sourceRevision,
    currentSourceRevision: item.currentSourceRevision ?? null,
    sourceStatus: null,
  };
}

export function itemViewFromQuestion(q: Question): ItemView {
  return {
    stem: q.stem,
    type: q.type,
    difficulty: q.difficulty,
    payload: q.payload,
    explanation: q.explanation ?? null,
    sourceRevision: null,
    currentSourceRevision: null,
    sourceStatus: q.status,
  };
}

export function toFormValues(version: ExamVersion): ComposerFormInput {
  return {
    durationMinutes: String(version.durationMinutes),
    items: version.items.map((item) => ({
      sourceQuestionId: item.sourceQuestionId,
      points: formatDecimalForInput(item.points),
      view: itemViewFromVersionItem(item),
    })),
  };
}
