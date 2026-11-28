import { forwardRef } from "react";
import type {
  InputHTMLAttributes,
  ReactNode,
  SelectHTMLAttributes,
  TextareaHTMLAttributes,
} from "react";

const controlBase =
  "w-full rounded-md border border-control bg-white px-[11px] py-2 text-[13.5px] text-ink placeholder:text-muted disabled:cursor-not-allowed disabled:opacity-60";

function join(...parts: Array<string | false | undefined>) {
  return parts.filter(Boolean).join(" ");
}

export function Field({
  label,
  htmlFor,
  hint,
  error,
  children,
  className,
}: {
  label: ReactNode;
  htmlFor: string;
  hint?: ReactNode;
  error?: string;
  children: ReactNode;
  className?: string;
}) {
  return (
    <div className={join("mb-3.5", className)}>
      <label htmlFor={htmlFor} className="mb-[5px] block text-[12.5px] font-semibold text-ink-soft">
        {label}
      </label>
      {children}
      {hint ? <p className="mt-1 text-xs text-muted">{hint}</p> : null}
      {error ? (
        <p role="alert" className="mt-1 text-xs text-rej">
          {error}
        </p>
      ) : null}
    </div>
  );
}

export const Input = forwardRef<HTMLInputElement, InputHTMLAttributes<HTMLInputElement>>(
  function Input({ className, ...rest }, ref) {
    return <input ref={ref} {...rest} className={join(controlBase, "min-h-[37px]", className)} />;
  },
);

export const Select = forwardRef<HTMLSelectElement, SelectHTMLAttributes<HTMLSelectElement>>(
  function Select({ className, children, ...rest }, ref) {
    return (
      <select ref={ref} {...rest} className={join(controlBase, "min-h-[37px]", className)}>
        {children}
      </select>
    );
  },
);

export const Textarea = forwardRef<HTMLTextAreaElement, TextareaHTMLAttributes<HTMLTextAreaElement>>(
  function Textarea({ className, ...rest }, ref) {
    return <textarea ref={ref} {...rest} className={join(controlBase, "min-h-[84px]", className)} />;
  },
);

export const Checkbox = forwardRef<
  HTMLInputElement,
  { label: ReactNode } & Omit<InputHTMLAttributes<HTMLInputElement>, "type">
>(function Checkbox({ label, className, ...rest }, ref) {
  return (
    <label className={join("inline-flex items-center gap-[7px] text-[13px]", className)}>
      <input ref={ref} {...rest} type="checkbox" className="size-4 shrink-0 accent-accent" />
      <span>{label}</span>
    </label>
  );
});

export const Radio = forwardRef<
  HTMLInputElement,
  { label: ReactNode } & Omit<InputHTMLAttributes<HTMLInputElement>, "type">
>(function Radio({ label, className, ...rest }, ref) {
  return (
    <label className={join("inline-flex items-center gap-[7px] text-[13px]", className)}>
      <input ref={ref} {...rest} type="radio" className="size-4 shrink-0 accent-accent" />
      <span>{label}</span>
    </label>
  );
});
