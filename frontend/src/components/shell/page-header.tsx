import Link from "next/link";
import type { ReactNode } from "react";

import { SidebarToggle } from "./sidebar-toggle";

/** The teal "board" at the top of every page: toggle, breadcrumb, title, actions and tabs. */
export function PageHeader({
  title,
  description,
  crumb,
  actions,
  tabs,
}: {
  title: ReactNode;
  description?: ReactNode;
  crumb?: { href: string; label: string };
  actions?: ReactNode;
  tabs?: ReactNode;
}) {
  return (
    <header data-surface="board" className="border-b-4 border-margin bg-board text-on-board print:hidden">
      <div className="flex flex-wrap items-end justify-between gap-4 px-4 pt-4 pb-5 sm:px-7 sm:pt-[22px] sm:pb-6">
        <div className="flex min-w-0 flex-1 items-start gap-3">
          <SidebarToggle />
          <div className="min-w-0 flex-1">
            {crumb ? (
              <div className="mb-1.5 text-[12.5px] text-on-board-muted">
                <Link href={crumb.href} className="text-on-board-muted underline-offset-2 hover:text-on-board hover:underline">
                  {crumb.label}
                </Link>
              </div>
            ) : null}
            <h1 className="text-[22px] leading-tight font-semibold sm:text-[26px]">{title}</h1>
            {description ? <div className="mt-1 text-[13px] text-on-board-muted">{description}</div> : null}
          </div>
        </div>
        {actions ? <div className="flex flex-wrap gap-2 max-sm:w-full">{actions}</div> : null}
      </div>
      {tabs ? <div className="relative -mt-2 overflow-x-auto px-4 sm:px-7">{tabs}</div> : null}
    </header>
  );
}
