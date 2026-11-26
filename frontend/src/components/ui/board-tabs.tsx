import { useRef, type KeyboardEvent, type ReactNode } from "react";

function tabId(id: string, key: string): string {
  return `${id}-tab-${key}`;
}

function panelId(id: string, key: string): string {
  return `${id}-panel-${key}`;
}

/** Props for the element that shows the active tab's content (only the active panel is rendered). */
export function boardTabPanelProps(id: string, active: string) {
  return {
    id: panelId(id, active),
    role: "tabpanel" as const,
    "aria-labelledby": tabId(id, active),
    tabIndex: 0,
  };
}

/**
 * ARIA tabs with automatic activation: arrow keys (wrapping), Home and End move focus and select;
 * only the selected tab is in the Tab order. Pair it with `boardTabPanelProps(id, active)`.
 */
export function BoardTabs({
  id,
  items,
  active,
  onSelect,
  className,
}: {
  id: string;
  items: { key: string; label: ReactNode }[];
  active: string;
  onSelect: (key: string) => void;
  className?: string;
}) {
  const refs = useRef(new Map<string, HTMLButtonElement>());

  function onKeyDown(event: KeyboardEvent<HTMLButtonElement>, index: number) {
    const last = items.length - 1;
    let next: number;
    switch (event.key) {
      case "ArrowRight":
        next = index === last ? 0 : index + 1;
        break;
      case "ArrowLeft":
        next = index === 0 ? last : index - 1;
        break;
      case "Home":
        next = 0;
        break;
      case "End":
        next = last;
        break;
      default:
        return;
    }
    event.preventDefault();
    const key = items[next].key;
    refs.current.get(key)?.focus();
    if (key !== active) onSelect(key);
  }

  return (
    <div
      role="tablist"
      className={["relative flex gap-1 overflow-x-auto px-4 sm:px-7", className].filter(Boolean).join(" ")}
    >
      {items.map((item, index) => {
        const selected = item.key === active;
        return (
          <button
            key={item.key}
            ref={(el) => {
              if (el) refs.current.set(item.key, el);
              else refs.current.delete(item.key);
            }}
            id={tabId(id, item.key)}
            type="button"
            role="tab"
            aria-selected={selected}
            aria-controls={selected ? panelId(id, item.key) : undefined}
            tabIndex={selected ? 0 : -1}
            onClick={() => onSelect(item.key)}
            onKeyDown={(event) => onKeyDown(event, index)}
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
