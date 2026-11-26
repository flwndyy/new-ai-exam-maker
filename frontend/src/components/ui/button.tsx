import Link from "next/link";
import { useId } from "react";
import type { ButtonHTMLAttributes, ReactNode } from "react";

export type ButtonVariant =
  | "primary"
  | "secondary"
  | "danger"
  | "approve"
  | "boardPrimary"
  | "boardSecondary";
export type ButtonSize = "md" | "sm";

const base =
  "inline-flex items-center justify-center gap-1.5 rounded-md border font-semibold whitespace-nowrap transition-colors duration-150 disabled:cursor-not-allowed disabled:opacity-45";

const variantClass: Record<ButtonVariant, string> = {
  primary: "border-transparent bg-accent text-white hover:bg-accent-strong",
  secondary: "border-line-strong bg-transparent text-ink hover:bg-tint",
  danger: "border-rej-line bg-transparent text-rej hover:bg-rej-bg",
  approve: "border-transparent bg-ok text-white hover:bg-ok-strong",
  boardPrimary: "border-transparent bg-white text-accent-strong hover:bg-on-board-muted",
  boardSecondary: "border-white/50 bg-transparent text-white hover:bg-white/15",
};

const sizeClass: Record<ButtonSize, string> = {
  md: "px-3.5 py-2 text-[13.5px]",
  sm: "px-2.5 py-[5px] text-[12.5px]",
};

function buttonClass(variant: ButtonVariant, size: ButtonSize, className?: string) {
  return [base, variantClass[variant], sizeClass[size], className].filter(Boolean).join(" ");
}

export type ButtonProps = ButtonHTMLAttributes<HTMLButtonElement> & {
  variant?: ButtonVariant;
  size?: ButtonSize;
  disabledReason?: string;
};

export function Button({
  variant = "primary",
  size = "md",
  disabledReason,
  className,
  type = "button",
  disabled,
  children,
  ...rest
}: ButtonProps) {
  const reasonId = useId();
  const explain = Boolean(disabled && disabledReason);
  return (
    <>
      <button
        {...rest}
        type={type}
        disabled={disabled}
        title={explain ? disabledReason : rest.title}
        aria-describedby={explain ? reasonId : rest["aria-describedby"]}
        className={buttonClass(variant, size, className)}
      >
        {children}
      </button>
      {explain ? (
        <span id={reasonId} className="sr-only">
          {disabledReason}
        </span>
      ) : null}
    </>
  );
}

export function ButtonLink({
  href,
  variant = "primary",
  size = "md",
  className,
  children,
}: {
  href: string;
  variant?: ButtonVariant;
  size?: ButtonSize;
  className?: string;
  children: ReactNode;
}) {
  return (
    <Link href={href} className={buttonClass(variant, size, className)}>
      {children}
    </Link>
  );
}
