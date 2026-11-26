import type { ReactNode } from "react";

export function BoardTabs({
  items,
  active,
  onSelect,
  className,
}: {
  items: { key: string; label: ReactNode }[];
  active: string;
  onSelect: (key: string) => void;
  className?: string;
}) {
  return (
    <div
      role="tablist"
      className={["relative flex gap-1 overflow-x-auto px-4 sm:px-7", className].filter(Boolean).join(" ")}
    >
      {items.map((item) => {
        const selected = item.key === active;
        return (
          <button
            key={item.key}
            type="button"
            role="tab"
            aria-selected={selected}
            onClick={() => onSelect(item.key)}
            className={
              selected
                ? "rounded-t-md bg-paper px-3.5 py-2 text-[13.5px] font-semibold whitespace-nowrap text-ink"
                : "rounded-t-md px-3.5 py-2 text-[13.5px] whitespace-nowrap text-on-board-muted transition-colors duration-150 hover:text-on-board"
            }
          >
            {item.label}
          </button>
        );
      })}
    </div>
  );
}
