import { act, cleanup, fireEvent, render, screen } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { vi as messages } from "@/messages/vi";
import { AppShell } from "./app-shell";
import { PageHeader } from "./page-header";
import { SideNav } from "./side-nav";
import { SidebarProvider } from "./sidebar-state";

const mocks = vi.hoisted(() => ({
  auth: {
    status: "signedIn" as string,
    me: null as null | {
      id: string;
      email: string;
      fullName: string;
      role: "teacher" | "head" | "admin";
      departmentId: string | null;
      departmentName: string | null;
      isActive: boolean;
    },
    logout: vi.fn(),
  },
  pathname: "/",
}));

vi.mock("next/navigation", () => ({ usePathname: () => mocks.pathname }));
vi.mock("@/lib/auth/auth-context", () => ({
  useAuth: () => mocks.auth,
  isPublicPath: (p: string) => p.replace(/\/+$/, "") === "/auth/callback",
}));

const STORAGE_KEY = "exammaker.ui.sidebar";

// Controllable matchMedia: `wide` is whether (min-width: 1024px) matches.
const media = { wide: true, listeners: new Set<() => void>() };
function setWide(wide: boolean) {
  media.wide = wide;
  act(() => {
    for (const l of [...media.listeners]) l();
  });
}

function Tree({ children, headerKey = "h" }: { children?: ReactNode; headerKey?: string }) {
  return (
    <SidebarProvider>
      <SideNav />
      <PageHeader key={headerKey} title="Tiêu đề" />
      {children}
    </SidebarProvider>
  );
}

const toggle = () => document.getElementById("app-sidebar-toggle") as HTMLButtonElement;
const nav = () => document.getElementById("app-sidebar");
const backdrop = () => document.querySelector("[data-sidebar-backdrop]");

beforeEach(() => {
  media.wide = true;
  media.listeners.clear();
  mocks.pathname = "/";
  mocks.auth.status = "signedIn";
  mocks.auth.me = {
    id: "u1",
    email: "u@example.invalid",
    fullName: "Trần Thu Hà",
    role: "teacher",
    departmentId: "d1",
    departmentName: "Tổ Toán",
    isActive: true,
  };
  mocks.auth.logout.mockReset();
  window.localStorage.clear();
  vi.stubGlobal("matchMedia", (query: string) => ({
    get matches() {
      return media.wide;
    },
    media: query,
    addEventListener: (_: string, l: () => void) => media.listeners.add(l),
    removeEventListener: (_: string, l: () => void) => media.listeners.delete(l),
  }));
});

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

describe("side navigation on a wide screen", () => {
  it("is open by default, closes from the toggle, and remembers the choice", () => {
    const { unmount } = render(<Tree />);
    expect(nav()).not.toBeNull();
    expect(toggle().getAttribute("aria-expanded")).toBe("true");
    expect(toggle().getAttribute("aria-controls")).toBe("app-sidebar");
    expect(toggle().getAttribute("aria-label")).toBe(messages.shell.toggleMenu);

    fireEvent.click(toggle());
    expect(nav()).toBeNull();
    expect(toggle().getAttribute("aria-expanded")).toBe("false");
    expect(window.localStorage.getItem(STORAGE_KEY)).toBe("closed");

    unmount();
    render(<Tree />);
    expect(nav()).toBeNull();
  });

  it("restores an open choice written earlier", () => {
    window.localStorage.setItem(STORAGE_KEY, "open");
    render(<Tree />);
    expect(nav()).not.toBeNull();
  });

  it("does not crash and stays open when reading localStorage throws", () => {
    vi.spyOn(Storage.prototype, "getItem").mockImplementation(() => {
      throw new Error("blocked");
    });
    render(<Tree />);
    expect(nav()).not.toBeNull();
    expect(toggle().getAttribute("aria-expanded")).toBe("true");
  });

  it("still toggles when writing localStorage throws", () => {
    vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => {
      throw new Error("blocked");
    });
    render(<Tree />);
    fireEvent.click(toggle());
    expect(nav()).toBeNull();
    fireEvent.click(toggle());
    expect(nav()).not.toBeNull();
  });
});

describe("side navigation on a narrow screen", () => {
  beforeEach(() => {
    media.wide = false;
  });

  it("is closed by default, opens as an overlay, and closes on Escape with focus back on the toggle", () => {
    render(<Tree />);
    expect(nav()).toBeNull();
    expect(toggle().getAttribute("aria-expanded")).toBe("false");

    fireEvent.click(toggle());
    expect(nav()).not.toBeNull();
    expect(nav()!.className).toContain("fixed");
    expect(backdrop()).not.toBeNull();
    expect(document.activeElement).toBe(nav()!.querySelector("a"));

    fireEvent.keyDown(document.activeElement ?? document.body, { key: "Escape" });
    expect(nav()).toBeNull();
    expect(document.activeElement).toBe(toggle());
    expect(window.localStorage.getItem(STORAGE_KEY)).toBeNull();
  });

  it("closes from the backdrop and returns focus to the toggle", () => {
    render(<Tree />);
    fireEvent.click(toggle());
    fireEvent.click(backdrop()!);
    expect(nav()).toBeNull();
    expect(document.activeElement).toBe(toggle());
  });

  it("closes when the current page's entry is chosen, returning focus to the toggle", () => {
    mocks.pathname = "/";
    render(<Tree />);
    fireEvent.click(toggle());
    fireEvent.click(screen.getByRole("link", { name: messages.nav.home }));
    expect(nav()).toBeNull();
    expect(document.activeElement).toBe(toggle());
  });

  it("keeps focus inside the overlay with Tab and Shift+Tab", () => {
    render(<Tree />);
    fireEvent.click(toggle());
    const focusables = Array.from(nav()!.querySelectorAll<HTMLElement>("a[href], button"));
    const first = focusables[0];
    const last = focusables[focusables.length - 1];

    last.focus();
    fireEvent.keyDown(last, { key: "Tab" });
    expect(document.activeElement).toBe(first);

    first.focus();
    fireEvent.keyDown(first, { key: "Tab", shiftKey: true });
    expect(document.activeElement).toBe(last);
  });

  it("closes when the pathname changes", () => {
    const { rerender } = render(<Tree />);
    fireEvent.click(toggle());
    expect(nav()).not.toBeNull();
    mocks.pathname = "/questions/";
    rerender(<Tree />);
    expect(nav()).toBeNull();
  });

  it("leaves no backdrop or focus trap when the window grows past 1024px with the overlay open", () => {
    render(<Tree />);
    fireEvent.click(toggle());
    expect(backdrop()).not.toBeNull();

    setWide(true);
    expect(backdrop()).toBeNull();
    expect(nav()).not.toBeNull();
    expect(nav()!.className).not.toContain("fixed");
    expect(toggle().getAttribute("aria-expanded")).toBe("true");

    // Escape and Tab no longer act on the (now inline) menu.
    fireEvent.keyDown(document.body, { key: "Escape" });
    expect(nav()).not.toBeNull();
    expect(window.localStorage.getItem(STORAGE_KEY)).toBeNull();
    const focusables = Array.from(nav()!.querySelectorAll<HTMLElement>("a[href], button"));
    const lastFocusable = focusables[focusables.length - 1];
    lastFocusable.focus();
    fireEvent.keyDown(lastFocusable, { key: "Tab" });
    expect(document.activeElement).toBe(lastFocusable);

    // Shrinking again starts closed rather than reopening the overlay.
    setWide(false);
    expect(nav()).toBeNull();
  });

  it("marks the overlay as a modal dialog that holds the navigation landmark", () => {
    render(<Tree />);
    fireEvent.click(toggle());
    const dialog = screen.getByRole("dialog", { name: messages.shell.navLabel });
    expect(dialog.id).toBe("app-sidebar");
    expect(dialog.getAttribute("aria-modal")).toBe("true");
    expect(dialog.querySelector("nav")?.getAttribute("aria-label")).toBe(messages.shell.navLabel);
  });

  it("drops the dialog semantics without moving focus when the window grows past 1024px", () => {
    render(<Tree />);
    fireEvent.click(toggle());
    const focused = document.activeElement;
    setWide(true);
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(nav()!.getAttribute("aria-modal")).toBeNull();
    expect(screen.getByRole("navigation", { name: messages.shell.navLabel })).toBeTruthy();
    expect(document.activeElement).toBe(focused);
  });

  it("returns focus to the toggle when the overlay closes because the pathname changes", () => {
    const { rerender } = render(<Tree />);
    fireEvent.click(toggle());
    expect(nav()!.contains(document.activeElement)).toBe(true);
    mocks.pathname = "/questions/";
    rerender(<Tree />);
    expect(nav()).toBeNull();
    expect(document.activeElement).toBe(toggle());
  });

  it("falls back to the new page's toggle when the one that opened the menu is gone", () => {
    const { rerender } = render(<Tree headerKey="a" />);
    fireEvent.click(toggle());
    mocks.pathname = "/questions/";
    rerender(<Tree headerKey="b" />);
    expect(nav()).toBeNull();
    expect(document.activeElement).toBe(toggle());
  });

  it("moves focus to the new page's toggle after navigating from an overlay entry", () => {
    const { rerender } = render(<Tree headerKey="a" />);
    fireEvent.click(toggle());
    fireEvent.click(screen.getByRole("link", { name: messages.nav.exams }));
    expect(nav()).toBeNull();
    mocks.pathname = "/exams/";
    rerender(<Tree headerKey="b" />);
    expect(document.activeElement).toBe(toggle());
  });

  it("returns the saved value when the window grows to a wide screen", () => {
    window.localStorage.setItem(STORAGE_KEY, "closed");
    render(<Tree />);
    expect(nav()).toBeNull();
    setWide(true);
    expect(nav()).toBeNull();
    expect(toggle().getAttribute("aria-expanded")).toBe("false");
  });
});

describe("menu entries", () => {
  it("shows the teacher no review link", () => {
    render(<Tree />);
    expect(screen.getByRole("link", { name: messages.nav.ai })).toBeTruthy();
    expect(screen.queryByRole("link", { name: messages.nav.review })).toBeNull();
  });

  it("shows the head the review link", () => {
    mocks.auth.me!.role = "head";
    render(<Tree />);
    expect(screen.getByRole("link", { name: messages.nav.review })).toBeTruthy();
  });

  it("shows the admin the usage link and not the AI generation link", () => {
    mocks.auth.me!.role = "admin";
    render(<Tree />);
    expect(screen.getByRole("link", { name: messages.nav.aiUsage })).toBeTruthy();
    expect(screen.getByRole("link", { name: messages.nav.admin })).toBeTruthy();
    expect(screen.queryByRole("link", { name: messages.nav.ai })).toBeNull();
  });

  it("marks the current entry with aria-current, tolerating a missing trailing slash", () => {
    mocks.auth.me!.role = "admin";
    mocks.pathname = "/admin/ai-usage";
    render(<Tree />);
    expect(screen.getByRole("link", { name: messages.nav.aiUsage }).getAttribute("aria-current")).toBe("page");
    expect(screen.getByRole("link", { name: messages.nav.admin }).getAttribute("aria-current")).toBeNull();
    expect(screen.getByRole("link", { name: messages.nav.home }).getAttribute("aria-current")).toBeNull();
  });

  it.each(["teacher", "head", "admin"] as const)("links the exams entry for %s", (role) => {
    mocks.auth.me!.role = role;
    render(<Tree />);
    expect(screen.getByRole("link", { name: messages.nav.exams }).getAttribute("href")).toMatch(/^\/exams\/?$/);
  });

  it("marks home on the root", () => {
    render(<Tree />);
    expect(screen.getByRole("link", { name: messages.nav.home }).getAttribute("aria-current")).toBe("page");
  });

  it("labels the nav and shows the user block with a logout button", () => {
    render(<Tree />);
    expect(screen.getByRole("navigation", { name: messages.shell.navLabel })).toBeTruthy();
    expect(screen.getByText("Trần Thu Hà")).toBeTruthy();
    const line = screen.getByText("Giáo viên, tổ Toán");
    expect(line.textContent).not.toContain(" · ");
    fireEvent.click(screen.getByRole("button", { name: messages.auth.logout }));
    expect(mocks.auth.logout).toHaveBeenCalledTimes(1);
  });

  it("shows only the role when the user has no department", () => {
    mocks.auth.me!.departmentName = null;
    render(<Tree />);
    expect(screen.getByText(messages.auth.roles.teacher)).toBeTruthy();
  });

  it("does not double the department prefix in the role line", () => {
    expect(messages.auth.roleLine("teacher", "Tổ Toán")).toBe("Giáo viên, tổ Toán");
    expect(messages.auth.roleLine("teacher", "Toán")).toBe("Giáo viên, tổ Toán");
    expect(messages.auth.roleLine("head", "tổ Hóa học")).toBe("Tổ trưởng, tổ Hóa học");
    expect(messages.auth.roleLine("admin", null)).toBe("Quản trị");
  });

  it.each(["teacher", "head"] as const)("gives %s the AI generation link but not the usage report", (role) => {
    mocks.auth.me!.role = role;
    render(<Tree />);
    expect(screen.getByRole("link", { name: messages.nav.ai }).getAttribute("href")).toMatch(/^\/ai\/?$/);
    expect(screen.queryByRole("link", { name: messages.nav.aiUsage })).toBeNull();
  });
});

describe("PageHeader", () => {
  it("renders the board with title, description, crumb, actions and tabs", () => {
    render(
      <SidebarProvider>
        <PageHeader
          title="Tiêu đề"
          description="Mô tả"
          crumb={{ href: "/exams/", label: "Đề thi" }}
          actions={<button>Hành động</button>}
          tabs={<span>Tab</span>}
        />
      </SidebarProvider>,
    );
    const heading = screen.getByRole("heading", { level: 1, name: "Tiêu đề" });
    const board = heading.closest("[data-surface='board']") as HTMLElement;
    expect(board).not.toBeNull();
    expect(board.className).toContain("print:hidden");
    expect(screen.getByText("Mô tả")).toBeTruthy();
    expect(screen.getByRole("link", { name: "Đề thi" }).getAttribute("href")).toMatch(/^\/exams\/?$/);
    expect(screen.getByRole("button", { name: "Hành động" })).toBeTruthy();
    expect(screen.getByText("Tab")).toBeTruthy();
    expect(board.querySelector("#app-sidebar-toggle")).not.toBeNull();
  });
});

describe("AppShell", () => {
  it("renders the menu and main when signed in", () => {
    render(
      <SidebarProvider>
        <AppShell>
          <p>Nội dung</p>
        </AppShell>
      </SidebarProvider>,
    );
    expect(nav()).not.toBeNull();
    expect(screen.getByRole("main").textContent).toContain("Nội dung");
  });

  it("renders no menu when signed out", () => {
    mocks.auth.status = "signedOut";
    mocks.auth.me = null;
    render(
      <SidebarProvider>
        <AppShell>
          <p>Nội dung</p>
        </AppShell>
      </SidebarProvider>,
    );
    expect(nav()).toBeNull();
    expect(screen.getByRole("main").textContent).toContain("Nội dung");
  });

  it("renders no menu on a public path even when signed in", () => {
    mocks.pathname = "/auth/callback/";
    render(
      <SidebarProvider>
        <AppShell>
          <p>Nội dung</p>
        </AppShell>
      </SidebarProvider>,
    );
    expect(nav()).toBeNull();
  });
});
