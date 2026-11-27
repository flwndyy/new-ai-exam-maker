import type { ReactNode } from "react";

export type NoticeTone = "warn" | "error" | "info" | "success";

const toneClass: Record<NoticeTone, string> = {
  warn: "border-l-wait bg-wait-bg text-wait-text",
  error: "border-l-rej bg-rej-bg text-rej",
  info: "border-l-accent bg-tint text-ink",
  success: "border-l-ok bg-ok-bg text-ok-strong",
};

export function Notice({
  tone,
  title,
  children,
  action,
  className,
}: {
  tone: NoticeTone;
  title?: ReactNode;
  children?: ReactNode;
  action?: ReactNode;
  className?: string;
}) {
  return (
    <div
      role={tone === "error" ? "alert" : "status"}
      className={[
        "flex flex-wrap items-start justify-between gap-3 rounded-r-md border-l-[3px] px-3 py-2.5 text-[12.5px]",
        toneClass[tone],
        className,
      ]
        .filter(Boolean)
        .join(" ")}
    >
      <div className="min-w-0 break-words">
        {title ? <p className="font-semibold">{title}</p> : null}
        {children ? <div>{children}</div> : null}
      </div>
      {action ? <div className="shrink-0">{action}</div> : null}
    </div>
  );
}
