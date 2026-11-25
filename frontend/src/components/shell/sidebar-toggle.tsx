"use client";

import { vi } from "@/messages/vi";
import { useSidebar } from "./sidebar-state";

/** The ☰ button on the page header board. Opens or closes the side menu. */
export function SidebarToggle() {
  const { open, toggle } = useSidebar();
  return (
    <button
      type="button"
      id="app-sidebar-toggle"
      aria-controls="app-sidebar"
      aria-expanded={open}
      aria-label={vi.shell.toggleMenu}
      onClick={toggle}
      className="inline-flex size-9 shrink-0 items-center justify-center self-start rounded-lg border border-white/50 bg-transparent text-on-board transition-colors duration-150 hover:bg-white/15"
    >
      <svg aria-hidden="true" viewBox="0 0 20 20" className="size-[18px]" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round">
        <path d="M3 5h14M3 10h14M3 15h14" />
      </svg>
    </button>
  );
}
