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

/** The vertical menu: inline on wide screens, a modal overlay on narrow ones. */
export function SideNav() {
  const { me, logout } = useAuth();
  const pathname = usePathname();
  const { open, narrow, close } = useSidebar();
  const panelRef = useRef<HTMLDivElement>(null);
  // undefined: nothing to restore; null: restore to the toggle (opener unknown).
  const openerRef = useRef<HTMLElement | null | undefined>(undefined);
  // Set when an overlay entry starts a navigation: the toggle that got focus back belongs to the
  // old page and unmounts, so focus moves to the new page's toggle once the route has changed.
  const refocusAfterNavRef = useRef(false);
  const overlay = narrow && open;

  // Leaving the page (route change) closes the overlay.
  useEffect(() => {
    close();
    if (!refocusAfterNavRef.current) return;
    refocusAfterNavRef.current = false;
    const current = document.activeElement;
    if (current === null || current === document.body) document.getElementById(TOGGLE_ID)?.focus();
  }, [pathname, close]);

  // Overlay only: remember who opened it, focus the first entry, close on Escape, keep Tab
  // inside the menu. Registered only while the overlay is showing, so growing the window
  // past 1024px (which turns it back into the inline menu) removes the trap.
  useEffect(() => {
    if (!overlay) return;
    const opener = document.activeElement;
    openerRef.current = opener instanceof HTMLElement && opener !== document.body ? opener : null;
    panelRef.current?.querySelector<HTMLElement>("a[href]")?.focus();

    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape") {
        event.preventDefault();
        close();
        return;
      }
      if (event.key !== "Tab") return;
      const focusables = Array.from(panelRef.current?.querySelectorAll<HTMLElement>(FOCUSABLE) ?? []);
      if (focusables.length === 0) return;
      const first = focusables[0];
      const last = focusables[focusables.length - 1];
      const active = document.activeElement;
      const inside = panelRef.current?.contains(active) ?? false;
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

  // Whatever closed the overlay (Escape, backdrop, a menu entry, a route change), focus goes
  // back to the element that opened it, or to the current page's toggle when that element is
  // gone or unknown (Safari does not focus a clicked button). Focus moved elsewhere is kept.
  useEffect(() => {
    if (open) return;
    const opener = openerRef.current;
    openerRef.current = undefined;
    if (opener === undefined) return;
    const current = document.activeElement;
    if (current !== null && current !== document.body) return;
    (opener?.isConnected ? opener : document.getElementById(TOGGLE_ID))?.focus();
  }, [open]);

  if (!open || !me) return null;

  const items = navItemsFor(me.role);
  const current = activeNavKey(items, pathname);

  const panelClass = overlay
    ? "fixed inset-y-0 left-0 z-40 flex w-[236px] flex-col overflow-y-auto border-r border-line bg-sidebar px-3 pt-[18px] pb-3.5 shadow-overlay print:hidden"
    : "sticky top-0 flex h-screen w-[236px] shrink-0 flex-col overflow-y-auto border-r border-line bg-sidebar px-3 pt-[18px] pb-3.5 print:hidden";

  return (
    <>
      {overlay ? (
        <button
          type="button"
          tabIndex={-1}
          aria-hidden="true"
          data-sidebar-backdrop=""
          className="fixed inset-0 z-30 cursor-default bg-ink/40 print:hidden"
          onClick={close}
        />
      ) : null}
      {/* Same element tree in both modes: crossing 1024px only swaps attributes and keeps focus. */}
      <div
        ref={panelRef}
        id="app-sidebar"
        role={overlay ? "dialog" : undefined}
        aria-modal={overlay ? true : undefined}
        aria-label={overlay ? vi.shell.navLabel : undefined}
        className={panelClass}
      >
        <nav aria-label={vi.shell.navLabel} className="flex flex-1 flex-col gap-0.5">
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
                      if (!overlay) return;
                      refocusAfterNavRef.current = item.key !== current;
                      close();
                    }}
                    className={[
                      "block rounded-[7px] px-3 py-2 text-sm transition-colors duration-150",
                      active ? "bg-accent font-semibold text-white" : "text-ink-soft hover:bg-sidebar-hover",
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
      </div>
    </>
  );
}
