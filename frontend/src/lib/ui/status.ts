import type { components } from "@/lib/api/schema";

type QuestionStatus = components["schemas"]["QuestionStatus"];
type ExamVersionStatus = components["schemas"]["ExamVersionStatus"];
type AiJobStatus = components["schemas"]["AiJobStatus"];
type AiVerification = components["schemas"]["AiVerification"];

export type Tone = "ok" | "wait" | "rej" | "draft" | "pub" | "arch";

function assertNever(value: never): never {
  throw new Error(`Unhandled status: ${String(value)}`);
}

export function questionTone(s: QuestionStatus): Tone {
  switch (s) {
    case "Draft":
      return "draft";
    case "Submitted":
      return "wait";
    case "Approved":
      return "ok";
    case "Rejected":
      return "rej";
    case "Archived":
      return "arch";
    default:
      return assertNever(s);
  }
}

export function examVersionTone(s: ExamVersionStatus): Tone {
  switch (s) {
    case "Draft":
      return "draft";
    case "Submitted":
      return "wait";
    case "Approved":
      return "ok";
    case "Rejected":
      return "rej";
    case "Published":
      return "pub";
    case "Archived":
      return "arch";
    default:
      return assertNever(s);
  }
}

export function aiJobTone(s: AiJobStatus): Tone {
  switch (s) {
    case "Queued":
      return "draft";
    case "Running":
      return "wait";
    case "Succeeded":
      return "ok";
    case "Failed":
      return "rej";
    default:
      return assertNever(s);
  }
}

/** Tone of the AI answer-check flag; "neutral" has no status colour, so it is not a `Tone`. */
export function aiVerificationTone(v: AiVerification): "ok" | "rej" | "neutral" {
  switch (v) {
    case "Agree":
      return "ok";
    case "Disagree":
      return "rej";
    case "NotChecked":
      return "neutral";
    default:
      return assertNever(v);
  }
}

export function userTone(isActive: boolean): Tone {
  return isActive ? "ok" : "arch";
}

export const toneClass: Record<
  Tone,
  {
    /** Left bar class, used on rows and cards. */
    stroke: string;
    /** Text class for use on white or paper. */
    text: string;
    /** Light background class. */
    bg: string;
    /** Text class for use on top of `bg`. */
    onBg: string;
  }
> = {
  ok: { stroke: "border-l-ok", text: "text-ok", bg: "bg-ok-bg", onBg: "text-ok-strong" },
  wait: { stroke: "border-l-wait", text: "text-wait-text", bg: "bg-wait-bg", onBg: "text-wait-text" },
  rej: { stroke: "border-l-rej", text: "text-rej", bg: "bg-rej-bg", onBg: "text-rej" },
  draft: { stroke: "border-l-draft", text: "text-muted", bg: "bg-tint", onBg: "text-ink-soft" },
  pub: { stroke: "border-l-pub", text: "text-pub", bg: "bg-pub-bg", onBg: "text-pub" },
  arch: { stroke: "border-l-arch", text: "text-arch-text", bg: "bg-tint", onBg: "text-ink-soft" },
};
