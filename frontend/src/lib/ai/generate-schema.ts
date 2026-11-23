import { z } from "zod";

import type { components } from "@/lib/api/schema";
import { vi } from "@/messages/vi";

type Grade = components["schemas"]["Grade"];

export const QUESTION_TYPES = ["MultipleChoice", "TrueFalse", "ShortAnswer", "Essay"] as const;
export const DIFFICULTIES = ["Recognition", "Understanding", "Application", "HighApplication"] as const;
export const GRADES = ["10", "11", "12"] as const;
export const MAX_NOTE_LENGTH = 1000;

/** Form schema for POST /ai/generate. Inputs are strings from form controls; the output is the request body. */
export function makeGenerateSchema(maxQuestionsPerJob: number) {
  const countMessage = vi.ai.validation.count(maxQuestionsPerJob);
  return z.object({
    subjectId: z.string().min(1, vi.ai.validation.required),
    grade: z.enum(GRADES, { error: vi.ai.validation.required }).transform((g) => Number(g) as Grade),
    topicId: z.string().min(1, vi.ai.validation.required),
    type: z.enum(QUESTION_TYPES, { error: vi.ai.validation.required }),
    difficulty: z.enum(DIFFICULTIES, { error: vi.ai.validation.required }),
    count: z
      .string()
      .regex(/^\d+$/, countMessage)
      .transform(Number)
      .pipe(z.number().int().min(1, countMessage).max(maxQuestionsPerJob, countMessage)),
    note: z
      .string()
      .transform((s) => s.trim())
      .pipe(z.string().max(MAX_NOTE_LENGTH, vi.ai.validation.noteTooLong))
      .transform((s) => (s === "" ? null : s)),
  });
}

export type GenerateFormInput = z.input<ReturnType<typeof makeGenerateSchema>>;
export type GenerateFormOutput = z.output<ReturnType<typeof makeGenerateSchema>>;
