import type { ReactNode } from "react";

export function DataTable({ children, className }: { children: ReactNode; className?: string }) {
  return (
    <div
      className={[
        "relative overflow-x-auto rounded-lg border border-line bg-white",
        "[&_table]:w-full [&_table]:border-collapse [&_table]:text-[13px]",
        "[&_th]:whitespace-nowrap [&_th]:border-b [&_th]:border-line [&_th]:bg-table-head [&_th]:px-3 [&_th]:py-[9px] [&_th]:text-left [&_th]:font-semibold [&_th]:text-ink-soft",
        "[&_td]:border-b [&_td]:border-line [&_td]:px-3 [&_td]:py-2.5 [&_td]:align-top",
        "[&_tr:last-child_td]:border-b-0",
        "[&_.num]:text-right [&_.num]:tabular-nums",
        className,
      ]
        .filter(Boolean)
        .join(" ")}
    >
      {children}
    </div>
  );
}
