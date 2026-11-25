import type { ReactNode } from "react";

export type TagTone = "neutral" | "ai" | "ok" | "rej";

const toneClass: Record<TagTone, string> = {
  neutral: "bg-tint text-ink-soft",
  ai: "bg-[#E7ECF1] text-ink",
  ok: "bg-ok-bg text-ok-strong",
  rej: "bg-rej-bg text-rej",
};

export function Tag({
  tone = "neutral",
  children,
  className,
}: {
  tone?: TagTone;
  children: ReactNode;
  className?: string;
}) {
  return (
    <span
      className={[
        "inline-block rounded px-[7px] py-px text-[11.5px] font-semibold",
        toneClass[tone],
        className,
      ]
        .filter(Boolean)
        .join(" ")}
    >
      {children}
    </span>
  );
}
