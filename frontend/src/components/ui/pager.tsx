import { vi as messages } from "@/messages/vi";
import { Button } from "./button";

export function Pager({
  page,
  totalPages,
  onPage,
  className,
}: {
  page: number;
  totalPages: number;
  onPage: (page: number) => void;
  className?: string;
}) {
  return (
    <nav
      aria-label={messages.common.pageLabel}
      className={["mt-3.5 flex items-center justify-between gap-3 text-[13px] text-muted", className]
        .filter(Boolean)
        .join(" ")}
    >
      <Button variant="secondary" size="sm" disabled={page <= 1} onClick={() => onPage(page - 1)}>
        {messages.common.previousPage}
      </Button>
      <span className="tabular-nums">{messages.common.pageOf(page, totalPages)}</span>
      <Button
        variant="secondary"
        size="sm"
        disabled={page >= totalPages}
        onClick={() => onPage(page + 1)}
      >
        {messages.common.nextPage}
      </Button>
    </nav>
  );
}
