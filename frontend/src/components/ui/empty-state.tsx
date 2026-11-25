import type { ReactNode } from "react";

export function EmptyState({
  title,
  description,
  action,
  busy,
  headingLevel = 3,
  className,
}: {
  title: ReactNode;
  description?: ReactNode;
  action?: ReactNode;
  busy?: boolean;
  /** Use 1 when the notice is the whole page (auth screens). */
  headingLevel?: 1 | 2 | 3;
  className?: string;
}) {
  const Heading = `h${headingLevel}` as const;
  return (
    <div
      role={busy ? "status" : undefined}
      className={[
        "flex min-h-[200px] flex-col items-center justify-center gap-2.5 rounded-lg border border-line bg-surface p-7 text-center",
        className,
      ]
        .filter(Boolean)
        .join(" ")}
    >
      {busy ? (
        <span
          aria-hidden="true"
          className="size-[26px] animate-spin rounded-full border-[3px] border-line border-t-accent"
        />
      ) : null}
      <Heading className="text-[17px] font-semibold">{title}</Heading>
      {description ? <p className="max-w-[42ch] text-[13.5px] text-muted">{description}</p> : null}
      {action ? <div className="mt-1">{action}</div> : null}
    </div>
  );
}
