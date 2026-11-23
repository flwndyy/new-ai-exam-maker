import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { RoleNav } from "@/components/role-nav";
import { vi as messages } from "@/messages/vi";

const auth = vi.hoisted(() => ({
  status: "signedIn",
  me: { id: "u1", email: "u@example.invalid", fullName: "U", role: "teacher", departmentId: "d1", isActive: true },
}));

vi.mock("@/lib/auth/auth-context", () => ({ useAuth: () => auth }));

describe("RoleNav AI links", () => {
  afterEach(cleanup);

  it.each(["teacher", "head"])("%s sees the AI generation link but not the usage report", (role) => {
    auth.me.role = role;
    render(<RoleNav />);
    // next/link drops the trailing slash in tests because next.config (trailingSlash) is not loaded.
    expect(screen.getByRole("link", { name: messages.nav.ai }).getAttribute("href")).toMatch(/^\/ai\/?$/);
    expect(screen.queryByRole("link", { name: messages.nav.aiUsage })).toBeNull();
  });

  it.each(["teacher", "head", "admin"])("%s sees the exams link", (role) => {
    auth.me.role = role;
    render(<RoleNav />);
    expect(screen.getByRole("link", { name: messages.nav.exams }).getAttribute("href")).toMatch(/^\/exams\/?$/);
  });

  it("admin sees the usage report but not the AI generation link", () => {
    auth.me.role = "admin";
    render(<RoleNav />);
    expect(screen.getByRole("link", { name: messages.nav.aiUsage }).getAttribute("href")).toMatch(/^\/admin\/ai-usage\/?$/);
    expect(screen.queryByRole("link", { name: messages.nav.ai })).toBeNull();
  });
});
