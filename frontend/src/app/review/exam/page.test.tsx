import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import ReviewExamPage from "@/app/review/exam/page";
import { vi as messages } from "@/messages/vi";

const VID = "00000000-0000-0000-0000-000000000099";
const ME = "00000000-0000-0000-0000-000000000001";
const OTHER = "00000000-0000-0000-0000-000000000002";
const SUBMITTED_AT = "2026-11-22T00:00:00.123Z";

const mocks = vi.hoisted(() => ({
  auth: {
    status: "signedIn",
    me: { id: "00000000-0000-0000-0000-000000000001", role: "head" as "teacher" | "head" | "admin" },
  },
  apiGet: vi.fn(),
  apiPost: vi.fn(),
}));

vi.mock("next/navigation", () => ({
  useSearchParams: () => new URLSearchParams(`id=${VID}`),
  useRouter: () => ({ replace: vi.fn() }),
  usePathname: () => "/",
}));
vi.mock("@/lib/auth/auth-context", () => ({ useAuth: () => mocks.auth }));
vi.mock("@/lib/api/client", () => ({
  api: { GET: mocks.apiGet, POST: mocks.apiPost, PUT: vi.fn(), PATCH: vi.fn(), DELETE: vi.fn() },
}));

function version(status: string, createdById = OTHER) {
  return {
    id: VID,
    examId: "E1",
    examTitle: "Đề kiểm tra",
    versionNo: 2,
    status,
    grade: 10,
    durationMinutes: 45,
    questionCount: 1,
    totalPoints: 10,
    submittedAt: SUBMITTED_AT,
    createdBy: { id: createdById, fullName: "Tác giả" },
    items: [],
    warnings: [],
  };
}

function loadVersion(v: ReturnType<typeof version>) {
  mocks.apiGet.mockResolvedValue({ data: v, error: undefined });
}

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <ReviewExamPage />
    </QueryClientProvider>,
  );
}

const btn = (name: string) => screen.queryByRole("button", { name }) as HTMLButtonElement | null;

describe("Review exam page", () => {
  let confirm: ReturnType<typeof vi.spyOn>;
  beforeEach(() => {
    mocks.apiGet.mockReset();
    mocks.apiPost.mockReset();
    mocks.auth.me.role = "head";
    confirm = vi.spyOn(window, "confirm").mockReturnValue(true);
  });
  afterEach(() => {
    cleanup();
    confirm.mockRestore();
  });

  it("approves a Submitted version only after the whole checklist is ticked, echoing submittedAt", async () => {
    loadVersion(version("Submitted"));
    mocks.apiPost.mockResolvedValue({ data: {}, error: undefined });
    renderPage();

    await waitFor(() => expect(btn(messages.review.approve)).not.toBeNull());
    const approve = btn(messages.review.approve)!;
    expect(approve.disabled).toBe(true);
    const hintId = approve.getAttribute("aria-describedby");
    expect(hintId).toBeTruthy();
    const hint = document.getElementById(hintId as string);
    expect(hint?.textContent).toBe(messages.review.checklistRequired);
    expect(hint?.classList.contains("sr-only")).toBe(false);

    for (const checkbox of screen.getAllByRole("checkbox")) fireEvent.click(checkbox);
    expect(btn(messages.review.approve)!.disabled).toBe(false);
    fireEvent.click(btn(messages.review.approve)!);

    await waitFor(() =>
      expect(mocks.apiPost).toHaveBeenCalledWith("/exam-versions/{vid}/approve", {
        params: { path: { vid: VID } },
        body: { submittedAt: SUBMITTED_AT, note: null },
      }),
    );
    expect(await screen.findByText(messages.review.approvedSuccess)).toBeTruthy();
  });

  it("requires a note to reject and sends the trimmed note", async () => {
    loadVersion(version("Submitted"));
    mocks.apiPost.mockResolvedValue({ data: {}, error: undefined });
    renderPage();
    await waitFor(() => expect(btn(messages.review.reject)).not.toBeNull());

    fireEvent.click(btn(messages.review.reject)!);
    expect(screen.getByText(messages.review.rejectNeedsNote)).toBeTruthy();
    expect(mocks.apiPost).not.toHaveBeenCalled();

    fireEvent.change(screen.getByPlaceholderText(messages.review.notePlaceholder), { target: { value: "  Sai đáp án  " } });
    fireEvent.click(btn(messages.review.reject)!);
    await waitFor(() =>
      expect(mocks.apiPost).toHaveBeenCalledWith("/exam-versions/{vid}/reject", {
        params: { path: { vid: VID } },
        body: { submittedAt: SUBMITTED_AT, note: "Sai đáp án" },
      }),
    );
    expect(await screen.findByText(messages.review.rejectedSuccess)).toBeTruthy();
  });

  it("blocks self-review: shows the notice and no approve or reject buttons", async () => {
    loadVersion(version("Submitted", ME));
    renderPage();
    expect(await screen.findByText(messages.review.selfReviewNotice)).toBeTruthy();
    expect(btn(messages.review.approve)).toBeNull();
    expect(btn(messages.review.reject)).toBeNull();
  });

  describe("board actions by role and status", () => {
    it("admin on Approved: archive then publish in DOM order; publish asks for confirmation", async () => {
      mocks.auth.me.role = "admin";
      loadVersion(version("Approved"));
      mocks.apiPost.mockResolvedValue({ data: {}, error: undefined });
      renderPage();

      const archive = await screen.findByRole("button", { name: messages.review.archive });
      const publish = screen.getByRole("button", { name: messages.review.publish });
      expect(archive.compareDocumentPosition(publish) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
      expect(btn(messages.review.unpublish)).toBeNull();

      confirm.mockReturnValueOnce(false);
      fireEvent.click(publish);
      expect(confirm).toHaveBeenCalledWith(messages.review.confirmPublishExam);
      expect(mocks.apiPost).not.toHaveBeenCalled();

      fireEvent.click(publish);
      await waitFor(() =>
        expect(mocks.apiPost).toHaveBeenCalledWith("/exam-versions/{vid}/publish", { params: { path: { vid: VID } } }),
      );
      expect(await screen.findByText(messages.review.publishedSuccess)).toBeTruthy();
    });

    it("head on Approved only gets archive", async () => {
      loadVersion(version("Approved"));
      renderPage();
      await screen.findByRole("button", { name: messages.review.archive });
      expect(btn(messages.review.publish)).toBeNull();
      expect(btn(messages.review.unpublish)).toBeNull();
    });

    it("admin on Published only gets unpublish, which confirms then posts", async () => {
      mocks.auth.me.role = "admin";
      loadVersion(version("Published"));
      mocks.apiPost.mockResolvedValue({ data: {}, error: undefined });
      renderPage();

      const unpublish = await screen.findByRole("button", { name: messages.review.unpublish });
      expect(btn(messages.review.archive)).toBeNull();
      expect(btn(messages.review.publish)).toBeNull();

      fireEvent.click(unpublish);
      expect(confirm).toHaveBeenCalledWith(messages.review.confirmUnpublishExam);
      await waitFor(() =>
        expect(mocks.apiPost).toHaveBeenCalledWith("/exam-versions/{vid}/unpublish", { params: { path: { vid: VID } } }),
      );
      expect(await screen.findByText(messages.review.unpublishedSuccess)).toBeTruthy();
    });
  });

  it("shows the server error and refetches the version after a 409", async () => {
    mocks.auth.me.role = "admin";
    loadVersion(version("Approved"));
    mocks.apiPost.mockResolvedValue({
      data: undefined,
      error: { code: "PublishedVersionExists", title: "Đề đã có phiên bản đang xuất bản." },
    });
    renderPage();

    const publish = await screen.findByRole("button", { name: messages.review.publish });
    const getsBefore = mocks.apiGet.mock.calls.length;
    fireEvent.click(publish);

    expect(await screen.findByText("Đề đã có phiên bản đang xuất bản.")).toBeTruthy();
    await waitFor(() => expect(mocks.apiGet.mock.calls.length).toBeGreaterThan(getsBefore));
  });

  describe("while an action is pending", () => {
    it("disables the board buttons", async () => {
      mocks.auth.me.role = "admin";
      loadVersion(version("Approved"));
      let resolve!: (v: unknown) => void;
      mocks.apiPost.mockReturnValue(new Promise((r) => (resolve = r)));
      renderPage();

      fireEvent.click(await screen.findByRole("button", { name: messages.review.archive }));
      await waitFor(() => expect(btn(messages.review.archive)!.disabled).toBe(true));
      expect(btn(messages.review.publish)!.disabled).toBe(true);

      await act(async () => resolve({ data: {}, error: undefined }));
    });

    it("disables the panel buttons", async () => {
      loadVersion(version("Submitted"));
      let resolve!: (v: unknown) => void;
      mocks.apiPost.mockReturnValue(new Promise((r) => (resolve = r)));
      renderPage();

      await waitFor(() => expect(btn(messages.review.reject)).not.toBeNull());
      fireEvent.change(screen.getByPlaceholderText(messages.review.notePlaceholder), { target: { value: "Lý do" } });
      fireEvent.click(btn(messages.review.reject)!);
      await waitFor(() => expect(btn(messages.review.reject)!.disabled).toBe(true));
      expect(btn(messages.review.approve)!.disabled).toBe(true);

      await act(async () => resolve({ data: {}, error: undefined }));
    });
  });
});
