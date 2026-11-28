import type { components } from "@/lib/api/schema";
import { vi } from "@/messages/vi";

type Role = components["schemas"]["Role"];

export type NavKey = "home" | "questions" | "ai" | "exams" | "review" | "aiUsage" | "admin";
export type NavItem = { key: NavKey; href: string; label: string };

const ALL: Record<NavKey, NavItem> = {
  home: { key: "home", href: "/", label: vi.nav.home },
  questions: { key: "questions", href: "/questions/", label: vi.nav.questions },
  ai: { key: "ai", href: "/ai/", label: vi.nav.ai },
  exams: { key: "exams", href: "/exams/", label: vi.nav.exams },
  review: { key: "review", href: "/review/", label: vi.nav.review },
  aiUsage: { key: "aiUsage", href: "/admin/ai-usage/", label: vi.nav.aiUsage },
  admin: { key: "admin", href: "/admin/", label: vi.nav.admin },
};

const KEYS_BY_ROLE: Record<Role, NavKey[]> = {
  teacher: ["home", "questions", "ai", "exams"],
  head: ["home", "questions", "ai", "exams", "review"],
  admin: ["home", "questions", "exams", "review", "aiUsage", "admin"],
};

/** Menu entries for a role. UX only: the API enforces permissions. */
export function navItemsFor(role: Role): NavItem[] {
  return KEYS_BY_ROLE[role].map((key) => ALL[key]);
}

/** Ensures exactly one trailing slash so `/admin` and `/admin/` compare equal. */
function withTrailingSlash(path: string): string {
  return path.replace(/\/+$/, "") + "/";
}

/**
 * The entry to highlight for a path. `home` only matches `/`; every other entry matches
 * by path prefix and the longest prefix wins (so `/admin/ai-usage` is not `/admin`).
 */
export function activeNavKey(items: NavItem[], pathname: string): NavKey | null {
  const path = withTrailingSlash(pathname);
  let best: NavItem | null = null;
  for (const item of items) {
    const matches = item.key === "home" ? path === "/" : path.startsWith(withTrailingSlash(item.href));
    if (matches && (best === null || item.href.length > best.href.length)) best = item;
  }
  return best?.key ?? null;
}
