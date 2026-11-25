import Link from "next/link";
import type { ReactNode } from "react";

import { toneClass, type Tone } from "@/lib/ui/status";

export function ListRow({
  tone,
  href,
  title,
  meta,
  reason,
  end,
  highlight,
  className,
}: {
  tone: Tone;
  href?: string;
  title: ReactNode;
  meta?: ReactNode;
  reason?: ReactNode;
  end?: ReactNode;
  highlight?: "error";
  className?: string;
}) {
  return (
    <div
      data-tone={tone}
      className={[
        "relative mb-2 flex min-w-0 max-w-full flex-col gap-2 rounded-r-md border-l-4 bg-white px-3.5 py-3 ring-1 has-[a:focus-visible]:ring-2 has-[a:focus-visible]:ring-accent sm:flex-row sm:items-start sm:gap-4",
        toneClass[tone].stroke,
        highlight === "error" ? "ring-rej" : "ring-line hover:ring-[#C9CED4]",
        className,
      ]
        .filter(Boolean)
        .join(" ")}
    >
      <div
        data-row-content
        className="min-w-0 max-w-full flex-1 overflow-x-auto break-words [&_.katex-display]:max-w-full [&_.katex-display]:overflow-x-auto"
      >
        <div className={tone === "arch" ? "font-medium text-muted" : "font-medium"}>
          {href ? (
            <Link href={href} className="focus-visible:outline-none after:absolute after:inset-0">
              {title}
            </Link>
          ) : (
            title
          )}
        </div>
        {meta ? <div className="mt-[3px] text-[12.5px] text-muted">{meta}</div> : null}
        {reason ? <div className="mt-[5px] text-[12.5px] text-rej">{reason}</div> : null}
      </div>
      {end ? (
        <div
          data-row-end
          className="relative z-10 flex max-w-full shrink-0 flex-row flex-wrap items-center gap-2 sm:flex-col sm:items-end sm:gap-1.5 sm:text-right"
        >
          {end}
        </div>
      ) : null}
    </div>
  );
}
