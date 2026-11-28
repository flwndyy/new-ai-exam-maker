"use client";

import {
  createContext,
  useCallback,
  useContext,
  useMemo,
  useState,
  useSyncExternalStore,
  type ReactNode,
} from "react";

const STORAGE_KEY = "exammaker.ui.sidebar";
const WIDE_QUERY = "(min-width: 1024px)";

export interface SidebarState {
  /** Whether the menu is currently visible (inline on wide screens, overlay on narrow ones). */
  open: boolean;
  /** True below 1024px, where the menu is an overlay that is closed by default. */
  narrow: boolean;
  toggle(): void;
  /** Closes the overlay. No effect on wide screens, where only the toggle changes the saved choice. */
  close(): void;
}

// Without a provider (isolated page tests) behave like an open menu on a wide screen.
const FALLBACK: SidebarState = { open: true, narrow: false, toggle() {}, close() {} };

const SidebarContext = createContext<SidebarState>(FALLBACK);

function readStored(): boolean {
  try {
    return window.localStorage.getItem(STORAGE_KEY) !== "closed";
  } catch {
    // Private mode or blocked storage: default to open.
    return true;
  }
}

function writeStored(open: boolean) {
  try {
    window.localStorage.setItem(STORAGE_KEY, open ? "open" : "closed");
  } catch {
    // The choice just won't persist.
  }
}

function subscribeNarrow(onChange: () => void) {
  if (typeof window.matchMedia !== "function") return () => {};
  const query = window.matchMedia(WIDE_QUERY);
  query.addEventListener("change", onChange);
  return () => query.removeEventListener("change", onChange);
}

function getNarrow() {
  return typeof window.matchMedia === "function" && !window.matchMedia(WIDE_QUERY).matches;
}

export function SidebarProvider({ children }: { children: ReactNode }) {
  // Server render assumes a wide screen. The provider renders no DOM of its own and the
  // menu only appears after sign-in on the client, so nothing hydrates against this guess.
  const narrow = useSyncExternalStore(subscribeNarrow, getNarrow, () => false);
  const [wideOpen, setWideOpen] = useState(() => (typeof window === "undefined" ? true : readStored()));
  const [overlayOpen, setOverlayOpen] = useState(false);
  const [lastNarrow, setLastNarrow] = useState(narrow);

  if (lastNarrow !== narrow) {
    // Crossing the breakpoint always starts the overlay closed; on a wide screen the
    // saved choice applies again.
    setLastNarrow(narrow);
    setOverlayOpen(false);
  }

  const toggle = useCallback(() => {
    if (narrow) {
      setOverlayOpen((value) => !value);
      return;
    }
    const next = !wideOpen;
    setWideOpen(next);
    writeStored(next);
  }, [narrow, wideOpen]);

  const close = useCallback(() => setOverlayOpen(false), []);

  const value = useMemo<SidebarState>(
    () => ({ open: narrow ? overlayOpen : wideOpen, narrow, toggle, close }),
    [narrow, overlayOpen, wideOpen, toggle, close],
  );

  return <SidebarContext.Provider value={value}>{children}</SidebarContext.Provider>;
}

export function useSidebar(): SidebarState {
  return useContext(SidebarContext);
}
