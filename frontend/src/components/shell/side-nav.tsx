"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import { useEffect, useRef } from "react";

import { Button } from "@/components/ui/button";
import { useAuth } from "@/lib/auth/auth-context";
import { vi } from "@/messages/vi";
import { activeNavKey, navItemsFor } from "./nav-items";
import { useSidebar } from "./sidebar-state";

const TOGGLE_ID = "app-sidebar-toggle";
const FOCUSABLE = "a[href], button:not([disabled])";

function focusToggle() {
  document.getElementById(TOGGLE_ID)?.focus();
}

/** The vertical menu: inline on wide screens, a dismissible overlay on narrow ones. */
export function SideNav() {
  const { me, logout } = useAuth();
  const pathname = usePathname();
  const { open, narrow, close } = useSidebar();
  const navRef = useRef<HTMLElement>(null);
  const overlay = narrow && open;

  // Leaving the page (route change) closes the overlay.
  useEffect(() => {
    close();
  }, [pathname, close]);

  // Overlay only: focus the first entry, close on Escape, keep Tab inside the menu.
  // Registered only while the overlay is showing, so growing the window past 1024px
  // (which turns it back into the inline menu) removes the trap.
  useEffect(() => {
    if (!overlay) return;
    navRef.current?.querySelector<HTMLElement>("a[href]")?.focus();

    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape") {
        event.preventDefault();
        close();
        focusToggle();
        return;
      }
      if (event.key !== "Tab") return;
      const focusables = Array.from(navRef.current?.querySelectorAll<HTMLElement>(FOCUSABLE) ?? []);
      if (focusables.length === 0) return;
      const first = focusables[0];
      const last = focusables[focusables.length - 1];
      const active = document.activeElement;
      const inside = navRef.current?.contains(active) ?? false;
      if (!inside || (event.shiftKey && active === first)) {
        event.preventDefault();
        (event.shiftKey ? last : first).focus();
      } else if (!event.shiftKey && active === last) {
        event.preventDefault();
        first.focus();
      }
    };
    document.addEventListener("keydown", onKeyDown);
    return () => document.removeEventListener("keydown", onKeyDown);
  }, [overlay, close]);

  if (!open || !me) return null;

  const items = navItemsFor(me.role);
  const current = activeNavKey(items, pathname);

  const navClass = overlay
    ? "fixed inset-y-0 left-0 z-40 flex w-[236px] flex-col gap-0.5 overflow-y-auto border-r border-line bg-sidebar px-3 pt-[18px] pb-3.5 shadow-[8px_0_30px_rgba(20,25,30,0.25)] print:hidden"
    : "sticky top-0 flex h-screen w-[236px] shrink-0 flex-col gap-0.5 overflow-y-auto border-r border-line bg-sidebar px-3 pt-[18px] pb-3.5 print:hidden";

  return (
    <>
      {overlay ? (
        <button
          type="button"
          tabIndex={-1}
          aria-hidden="true"
          data-sidebar-backdrop=""
          className="fixed inset-0 z-30 cursor-default bg-ink/40 print:hidden"
          onClick={() => {
            close();
            focusToggle();
          }}
        />
      ) : null}
      <nav ref={navRef} id="app-sidebar" aria-label={vi.shell.navLabel} className={navClass}>
        <div className="px-3 pt-1 pb-[18px] text-[15px] font-bold text-ink">{vi.app.name}</div>
        <ul className="flex flex-col gap-0.5">
          {items.map((item) => {
            const active = item.key === current;
            return (
              <li key={item.key}>
                <Link
                  href={item.href}
                  aria-current={active ? "page" : undefined}
                  onClick={() => {
                    if (overlay) {
                      close();
                      focusToggle();
                    }
                  }}
                  className={[
                    "block rounded-[7px] px-3 py-2 text-sm transition-colors duration-150",
                    active ? "bg-accent font-semibold text-white" : "text-ink-soft hover:bg-[#E7EAEC]",
                  ].join(" ")}
                >
                  {item.label}
                </Link>
              </li>
            );
          })}
        </ul>
        <div className="mt-auto border-t border-line px-3 pt-3.5 text-[13px]">
          <b className="block font-semibold text-ink">{me.fullName}</b>
          <span className="block text-[12.5px] text-ink-soft">{vi.auth.roleLine(me.role, me.departmentName ?? null)}</span>
          <Button variant="secondary" size="sm" className="mt-2.5" onClick={() => void logout()}>
            {vi.auth.logout}
          </Button>
        </div>
      </nav>
    </>
  );
}
