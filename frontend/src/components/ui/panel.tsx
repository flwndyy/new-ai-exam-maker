import type { ReactNode } from "react";

export function Panel({
  title,
  aside,
  children,
  className,
}: {
  title?: ReactNode;
  aside?: ReactNode;
  children?: ReactNode;
  className?: string;
}) {
  return (
    <section
      className={["rounded-lg border border-line bg-surface p-[18px]", className]
        .filter(Boolean)
        .join(" ")}
    >
      {title || aside ? (
        <div className="mb-3 flex items-baseline justify-between gap-2">
          {title ? <h2 className="text-[15px] font-semibold">{title}</h2> : <span />}
          {aside ? <div className="text-[12.5px] text-muted">{aside}</div> : null}
        </div>
      ) : null}
      {children}
    </section>
  );
}
