import { useId } from "react";
import type { ReactNode, SelectHTMLAttributes } from "react";

export function FilterBar({ children, className }: { children: ReactNode; className?: string }) {
  return (
    <div className={["mb-3.5 flex flex-wrap items-center gap-2", className].filter(Boolean).join(" ")}>
      {children}
    </div>
  );
}

export function FilterSelect({
  label,
  className,
  children,
  ...rest
}: SelectHTMLAttributes<HTMLSelectElement> & { label: string }) {
  const id = useId();
  return (
    <div className="inline-block">
      <label htmlFor={id} className="sr-only">
        {label}
      </label>
      <select
        id={id}
        {...rest}
        className={["rounded-md bg-tint px-[11px] py-1.5 text-[13px] text-ink", className]
          .filter(Boolean)
          .join(" ")}
      >
        {children}
      </select>
    </div>
  );
}
