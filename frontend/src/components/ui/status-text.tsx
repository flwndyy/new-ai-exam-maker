import type { ReactNode } from "react";

import { toneClass, type Tone } from "@/lib/ui/status";

export function StatusText({
  tone,
  children,
  className,
}: {
  tone: Tone;
  children: ReactNode;
  className?: string;
}) {
  return (
    <span
      className={["whitespace-nowrap text-[12.5px] font-semibold", toneClass[tone].text, className]
        .filter(Boolean)
        .join(" ")}
    >
      {children}
    </span>
  );
}
