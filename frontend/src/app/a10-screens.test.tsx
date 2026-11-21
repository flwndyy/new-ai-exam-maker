import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import AdminPage from "@/app/admin/page";
import ReviewQuestionPage from "@/app/review/question/page";
import { RoleNav } from "@/components/role-nav";
import { vi as messages } from "@/messages/vi";

const mocks = vi.hoisted(() => ({
  auth: {
    status: "signedIn",
    me: {
      id: "00000000-0000-0000-0000-000000000001",
      email: "user@example.invalid",
      fullName: "Synthetic User",
      role: "teacher" as "teacher" | "head" | "admin",
      departmentId: "00000000-0000-0000-0000-000000000010",
      isActive: true,
    },
  },
  searchId: "00000000-0000-0000-0000-000000000099",
  apiGet: vi.fn(),
  apiPost: vi.fn(),
}));

vi.mock("next/navigation", () => ({
  useSearchParams: () => new URLSearchParams(mocks.searchId ? `id=${mocks.searchId}` : ""),
  useRouter: () => ({ replace: vi.fn() }),
}));

vi.mock("@/lib/auth/auth-context", () => ({
  useAuth: () => mocks.auth,
}));

vi.mock("@/lib/api/client", () => ({
  api: {
    GET: mocks.apiGet,
    POST: mocks.apiPost,
    PUT: vi.fn(),
    PATCH: vi.fn(),
    DELETE: vi.fn(),
  },
}));

function renderWithQuery(ui: ReactNode) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(<QueryClientProvider client={client}>{ui}</QueryClientProvider>);
}

describe("A10 admin, review, and role navigation screens", () => {
  beforeEach(() => {
    mocks.apiGet.mockReset();
    mocks.apiPost.mockReset();
    mocks.searchId = "00000000-0000-0000-0000-000000000099";
    mocks.auth.me.role = "teacher";
  });

  afterEach(() => {
    cleanup();
  });

  it("renders role-specific navigation links for teacher, head, and admin", () => {
    mocks.auth.me.role = "teacher";
    const { rerender } = render(<RoleNav />);
    expect(screen.getByText(messages.nav.questions)).toBeTruthy();
    expect(screen.queryByText(messages.nav.review)).toBeNull();
    expect(screen.queryByText(messages.nav.admin)).toBeNull();

    mocks.auth.me.role = "head";
    rerender(<RoleNav />);
    expect(screen.getByText(messages.nav.questions)).toBeTruthy();
    expect(screen.getByText(messages.nav.review)).toBeTruthy();
    expect(screen.queryByText(messages.nav.admin)).toBeNull();

    mocks.auth.me.role = "admin";
    rerender(<RoleNav />);
    expect(screen.queryByText(messages.nav.questions)).toBeNull();
    expect(screen.getByText(messages.nav.review)).toBeTruthy();
    expect(screen.getByText(messages.nav.admin)).toBeTruthy();
  });

  it("blocks non-admin users from AdminPage and shows 4 tabs for admin", async () => {
    mocks.auth.me.role = "head";
    renderWithQuery(<AdminPage />);
    expect(screen.getByText(messages.common.forbidden)).toBeTruthy();
    cleanup();

    mocks.auth.me.role = "admin";
    mocks.apiGet.mockResolvedValue({ data: { items: [] }, error: undefined });
    renderWithQuery(<AdminPage />);
    expect(screen.getByText(messages.admin.usersTab)).toBeTruthy();
    expect(screen.getByText(messages.admin.departmentsTab)).toBeTruthy();
    expect(screen.getByText(messages.admin.subjectsTab)).toBeTruthy();
    expect(screen.getByText(messages.admin.topicsTab)).toBeTruthy();
    await waitFor(() => expect(mocks.apiGet).toHaveBeenCalled());
  });

  it("enforces review checklist, rejection note, and sends submittedAt when approving", async () => {
    mocks.auth.me.role = "head";
    const submittedAt = "2026-11-22T00:00:00.000Z";
    mocks.apiGet.mockResolvedValue({
      data: {
        id: mocks.searchId,
        subjectId: "s1",
        topicId: "t1",
        departmentId: "d1",
        grade: 10,
        type: "MultipleChoice",
        difficulty: "Recognition",
        stem: "Solve $x + 1 = 2$",
        payload: {
          options: [
            { label: "A", text: "1" },
            { label: "B", text: "2" },
            { label: "C", text: "3" },
            { label: "D", text: "4" },
          ],
          correctLabel: "A",
        },
        explanation: null,
        status: "Submitted",
        revision: 1,
        source: "AI",
        aiVerification: "Disagree",
        aiVerificationNote: "Check sign",
        aiReviewedByAuthor: true,
        createdBy: { id: "other", fullName: "Author" },
        submittedAt,
        createdAt: submittedAt,
        updatedAt: submittedAt,
      },
      error: undefined,
    });
    mocks.apiPost.mockResolvedValue({ data: {}, error: undefined });

    renderWithQuery(<ReviewQuestionPage />);

    await waitFor(() => expect(screen.getByText(messages.review.approve)).toBeTruthy());
    expect(screen.getByText(/AI nghi ngờ đáp án/)).toBeTruthy();

    fireEvent.click(screen.getByText(messages.review.approve));
    expect(screen.getByText(messages.review.checklistRequired)).toBeTruthy();
    expect(mocks.apiPost).not.toHaveBeenCalled();

    fireEvent.click(screen.getByText(messages.review.reject));
    expect(screen.getByText(messages.review.rejectNeedsNote)).toBeTruthy();
    expect(mocks.apiPost).not.toHaveBeenCalled();

    for (const checkbox of screen.getAllByRole("checkbox")) {
      fireEvent.click(checkbox);
    }
    fireEvent.click(screen.getByText(messages.review.approve));

    await waitFor(() =>
      expect(mocks.apiPost).toHaveBeenCalledWith("/questions/{id}/approve", {
        params: { path: { id: mocks.searchId } },
        body: { submittedAt, note: null },
      }),
    );
  });
});
