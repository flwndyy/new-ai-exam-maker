import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import AdminPage from "@/app/admin/page";
import QuestionDetailPage from "@/app/questions/detail/page";
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
  apiPut: vi.fn(),
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
    PUT: mocks.apiPut,
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
    mocks.apiPut.mockReset();
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

  it("resets review checklist and note when submittedAt changes to a new round", async () => {
    mocks.auth.me.role = "head";
    const round1 = "2026-11-22T00:00:00.000Z";
    const round2 = "2026-11-22T01:00:00.000Z";
    const baseQuestion = {
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
      source: "Manual",
      aiVerification: "NotChecked",
      aiVerificationNote: null,
      aiReviewedByAuthor: false,
      createdBy: { id: "other", fullName: "Author" },
      submittedAt: round1,
      createdAt: round1,
      updatedAt: round1,
    };

    mocks.apiGet.mockResolvedValue({ data: baseQuestion, error: undefined });
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={client}>
        <ReviewQuestionPage />
      </QueryClientProvider>,
    );

    await waitFor(() => expect(screen.getAllByRole("checkbox")).toHaveLength(3));
    for (const checkbox of screen.getAllByRole("checkbox")) {
      fireEvent.click(checkbox);
      expect((checkbox as HTMLInputElement).checked).toBe(true);
    }

    mocks.apiGet.mockResolvedValue({ data: { ...baseQuestion, submittedAt: round2, updatedAt: round2 }, error: undefined });
    await client.invalidateQueries({ queryKey: ["question", mocks.searchId] });

    await waitFor(() => {
      for (const checkbox of screen.getAllByRole("checkbox")) {
        expect((checkbox as HTMLInputElement).checked).toBe(false);
      }
    });
  });

  it("preserves full Essay rubric on save, auto-saves before submit, and shows retry on load error", async () => {
    mocks.auth.me.role = "teacher";
    mocks.apiGet.mockImplementation(async (path: string) => {
      if (path === "/questions/{id}") {
        return { data: undefined, error: { title: "Không tìm thấy câu hỏi." } };
      }
      return { data: { items: [] }, error: undefined };
    });

    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={client}>
        <QuestionDetailPage />
      </QueryClientProvider>,
    );

    await waitFor(() => expect(screen.getByText("Không tìm thấy câu hỏi.")).toBeTruthy());
    expect(screen.getByText(messages.auth.retry)).toBeTruthy();
    expect(screen.queryByText(messages.questions.createTitle)).toBeNull();
    cleanup();

    const loadedEssay = {
      id: mocks.searchId,
      subjectId: "s1",
      topicId: "t1",
      departmentId: "d1",
      grade: 10,
      type: "Essay",
      difficulty: "Application",
      stem: "Original essay",
      payload: {
        modelAnswer: "Model",
        rubric: [
          { criterion: "Step 1", points: 0.5 },
          { criterion: "Step 2", points: 1.5 },
        ],
      },
      explanation: null,
      status: "Draft",
      revision: 1,
      source: "Manual",
      aiVerification: "NotChecked",
      aiVerificationNote: null,
      aiReviewedByAuthor: false,
      createdBy: { id: mocks.auth.me.id, fullName: "Teacher" },
      submittedAt: null,
      createdAt: "2026-11-22T00:00:00.000Z",
      updatedAt: "2026-11-22T00:00:00.000Z",
    };

    mocks.apiGet.mockImplementation(async (path: string) => {
      if (path === "/questions/{id}") return { data: loadedEssay, error: undefined };
      if (path === "/subjects") return { data: { items: [{ id: "s1", name: "Toán", departmentId: "d1" }] }, error: undefined };
      if (path === "/topics") return { data: { items: [{ id: "t1", subjectId: "s1", grade: 10, name: "Chương 1", sortOrder: 1 }] }, error: undefined };
      return { data: { items: [] }, error: undefined };
    });
    mocks.apiPut.mockResolvedValue({ data: { ...loadedEssay, stem: "Edited essay" }, error: undefined });
    mocks.apiPost.mockResolvedValue({ data: {}, error: undefined });

    renderWithQuery(<QuestionDetailPage />);
    await waitFor(() => expect(screen.getByDisplayValue("Original essay")).toBeTruthy());

    fireEvent.change(screen.getByDisplayValue("Original essay"), { target: { value: "Edited essay" } });
    fireEvent.click(screen.getByText(messages.questions.submit));

    await waitFor(() => expect(mocks.apiPut).toHaveBeenCalledTimes(1));
    const putBody = mocks.apiPut.mock.calls[0][1].body;
    expect(putBody.stem).toBe("Edited essay");
    expect(putBody.payload.rubric).toEqual([
      { criterion: "Step 1", points: 0.5 },
      { criterion: "Step 2", points: 1.5 },
    ]);
    await waitFor(() =>
      expect(mocks.apiPost).toHaveBeenCalledWith("/questions/{id}/submit", { params: { path: { id: mocks.searchId } } }),
    );
  });

  it("updates AI review state from PUT even when submit fails and allows 0.1 rubric points", async () => {
    mocks.auth.me.role = "teacher";
    const loadedAiEssay = {
      id: mocks.searchId,
      subjectId: "s1",
      topicId: "t1",
      departmentId: "d1",
      grade: 10,
      type: "Essay",
      difficulty: "Application",
      stem: "AI essay stem",
      payload: {
        modelAnswer: "Model",
        rubric: [{ criterion: "Fine grain", points: 0.1 }],
      },
      explanation: null,
      status: "Draft",
      revision: 1,
      source: "AI",
      aiVerification: "Agree",
      aiVerificationNote: null,
      aiReviewedByAuthor: true,
      createdBy: { id: mocks.auth.me.id, fullName: "Teacher" },
      submittedAt: null,
      createdAt: "2026-11-22T00:00:00.000Z",
      updatedAt: "2026-11-22T00:00:00.000Z",
    };

    const updatedAiEssay = {
      ...loadedAiEssay,
      stem: "AI essay edited",
      aiVerification: "NotChecked",
      aiReviewedByAuthor: false,
      updatedAt: "2026-11-22T00:05:00.000Z",
    };

    mocks.apiGet.mockImplementation(async (path: string) => {
      if (path === "/questions/{id}") return { data: loadedAiEssay, error: undefined };
      if (path === "/subjects") return { data: { items: [{ id: "s1", name: "Toán", departmentId: "d1" }] }, error: undefined };
      if (path === "/topics") return { data: { items: [{ id: "t1", subjectId: "s1", grade: 10, name: "Chương 1", sortOrder: 1 }] }, error: undefined };
      return { data: { items: [] }, error: undefined };
    });
    mocks.apiPut.mockResolvedValue({ data: updatedAiEssay, error: undefined });
    mocks.apiPost.mockResolvedValue({ data: undefined, error: { title: "Cần rà soát lại câu AI trước khi gửi duyệt." } });

    renderWithQuery(<QuestionDetailPage />);
    await waitFor(() => expect(screen.getByDisplayValue("AI essay stem")).toBeTruthy());
    expect(screen.queryByText(messages.questions.markReviewed)).toBeNull();

    fireEvent.change(screen.getByDisplayValue("AI essay stem"), { target: { value: "AI essay edited" } });
    fireEvent.click(screen.getByText(messages.questions.submit));

    await waitFor(() => expect(mocks.apiPut).toHaveBeenCalledTimes(1));
    expect(mocks.apiPut.mock.calls[0][1].body.payload.rubric[0].points).toBe(0.1);
    await waitFor(() => expect(screen.getByText("Cần rà soát lại câu AI trước khi gửi duyệt.")).toBeTruthy());
    expect(screen.getByText(messages.questions.markReviewed)).toBeTruthy();
  });

  it("hides archive button for peer teachers and rejected questions, shows it for owner/head/admin on Approved, and rejects blank rubric criterion", async () => {
    mocks.auth.me.role = "teacher";
    const peerQuestion = {
      id: mocks.searchId,
      subjectId: "s1",
      topicId: "t1",
      departmentId: mocks.auth.me.departmentId,
      grade: 10,
      type: "Essay",
      difficulty: "Application",
      stem: "Peer question",
      payload: {
        modelAnswer: "Model",
        rubric: [{ criterion: "   ", points: 1 }],
      },
      explanation: null,
      status: "Approved",
      revision: 1,
      source: "Manual",
      aiVerification: "NotChecked",
      aiVerificationNote: null,
      aiReviewedByAuthor: false,
      createdBy: { id: "peer-teacher-id", fullName: "Peer Teacher" },
      submittedAt: null,
      createdAt: "2026-11-22T00:00:00.000Z",
      updatedAt: "2026-11-22T00:00:00.000Z",
    };

    mocks.apiGet.mockImplementation(async (path: string) => {
      if (path === "/questions/{id}") return { data: peerQuestion, error: undefined };
      return { data: { items: [] }, error: undefined };
    });

    renderWithQuery(<QuestionDetailPage />);
    await waitFor(() => expect(screen.getByDisplayValue("Peer question")).toBeTruthy());
    // Peer teacher cannot archive another author's Approved question.
    expect(screen.queryByText(messages.questions.archive)).toBeNull();
    cleanup();

    // Owner on Rejected question cannot archive, and saving a blank rubric criterion shows a validation error.
    mocks.apiGet.mockImplementation(async (path: string) => {
      if (path === "/questions/{id}") {
        return {
          data: {
            ...peerQuestion,
            status: "Rejected",
            createdBy: { id: mocks.auth.me.id, fullName: "Owner" },
          },
          error: undefined,
        };
      }
      if (path === "/subjects") return { data: { items: [{ id: "s1", name: "Toán", departmentId: "d1" }] }, error: undefined };
      if (path === "/topics") return { data: { items: [{ id: "t1", subjectId: "s1", grade: 10, name: "Chương 1", sortOrder: 1 }] }, error: undefined };
      return { data: { items: [] }, error: undefined };
    });

    renderWithQuery(<QuestionDetailPage />);
    await waitFor(() => expect(screen.getByDisplayValue("Peer question")).toBeTruthy());
    expect(screen.queryByText(messages.questions.archive)).toBeNull();

    await waitFor(() => expect(screen.getByText("Chương 1")).toBeTruthy());
    fireEvent.click(screen.getByText(messages.common.save));
    await waitFor(() => expect(screen.getByText(messages.questions.criterionRequired)).toBeTruthy());
    expect(mocks.apiPut).not.toHaveBeenCalled();
  });

  it("hides review decision buttons and shows self-review notice when a head views their own submitted question", async () => {
    mocks.auth.me.role = "head";
    const submittedAt = "2026-11-22T00:00:00.000Z";
    mocks.apiGet.mockImplementation(async (path: string) => {
      if (path === "/questions/{id}") {
        return {
          data: {
            id: mocks.searchId,
            subjectId: "s1",
            topicId: "t1",
            departmentId: mocks.auth.me.departmentId,
            grade: 10,
            type: "ShortAnswer",
            difficulty: "HighApplication",
            stem: "Self authored question",
            payload: {
              answerType: "number",
              acceptedAnswers: ["9.8"],
              unit: "m/s2",
              tolerance: 0.05,
            },
            explanation: "Gravity",
            status: "Submitted",
            revision: 1,
            source: "Manual",
            aiVerification: "NotChecked",
            aiVerificationNote: null,
            aiReviewedByAuthor: false,
            createdBy: { id: mocks.auth.me.id, fullName: "Head Self" },
            submittedAt,
            createdAt: submittedAt,
            updatedAt: submittedAt,
          },
          error: undefined,
        };
      }
      if (path === "/topics") {
        return {
          data: { items: [{ id: "t1", subjectId: "s1", grade: 10, name: "Động học", sortOrder: 1 }] },
          error: undefined,
        };
      }
      return { data: { items: [] }, error: undefined };
    });

    renderWithQuery(<ReviewQuestionPage />);
    await waitFor(() => expect(screen.getByText(messages.review.selfReviewNotice)).toBeTruthy());
    expect(screen.queryByText(messages.review.approve)).toBeNull();
    expect(screen.queryByText(messages.review.reject)).toBeNull();
    // Topic name, difficulty, and ShortAnswer tolerance are all rendered.
    expect(screen.getByText(/Vận dụng cao/)).toBeTruthy();
    await waitFor(() => expect(screen.getByText(/Động học/)).toBeTruthy());
    expect(screen.getByText(/±0.05/)).toBeTruthy();
  });
});
