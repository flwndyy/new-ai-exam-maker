import type { ReactNode } from "react";

export function MetaBar({
  items,
  actions,
  className,
}: {
  items: { label: ReactNode; value: ReactNode }[];
  actions?: ReactNode;
  className?: string;
}) {
  return (
    <div
      className={[
        "mb-[18px] flex flex-wrap items-end gap-x-[22px] gap-y-3 rounded-lg border border-line bg-white px-4 py-3",
        className,
      ]
        .filter(Boolean)
        .join(" ")}
    >
      {items.map((item, i) => (
        <div key={i}>
          <div className="text-xs text-muted">{item.label}</div>
          <div className="text-[17px] font-semibold tabular-nums">{item.value}</div>
        </div>
      ))}
      {actions ? <div className="ml-auto flex flex-wrap items-center gap-2">{actions}</div> : null}
    </div>
  );
}
