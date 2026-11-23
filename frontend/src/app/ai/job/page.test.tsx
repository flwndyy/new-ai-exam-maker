import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import JobPage from "@/app/ai/job/page";
import { canMarkReviewed } from "@/components/ai/generated-question-card";
import { vi as messages } from "@/messages/vi";

const ME = "00000000-0000-0000-0000-000000000001";
const OTHER = "00000000-0000-0000-0000-000000000002";
const DEPT = "00000000-0000-0000-0000-000000000010";
const SUBJECT = "00000000-0000-0000-0000-000000000020";
const TOPIC = "00000000-0000-0000-0000-000000000030";
const JOB = "00000000-0000-0000-0000-0000000000a1";
const Q1 = "00000000-0000-0000-0000-0000000000b1";
const Q2 = "00000000-0000-0000-0000-0000000000b2";

const mocks = vi.hoisted(() => ({
  auth: {
    status: "signedIn",
    me: {
      id: "00000000-0000-0000-0000-000000000001",
      email: "user@example.invalid",
      fullName: "Synthetic User",
      role: "teacher",
      departmentId: "00000000-0000-0000-0000-000000000010",
      isActive: true,
    },
  },
  apiGet: vi.fn(),
  apiPost: vi.fn(),
}));

vi.mock("next/navigation", () => ({
  useSearchParams: () => new URLSearchParams("id=00000000-0000-0000-0000-0000000000a1"),
  useRouter: () => ({ push: vi.fn(), replace: vi.fn() }),
}));
vi.mock("@/lib/auth/auth-context", () => ({ useAuth: () => mocks.auth }));
vi.mock("@/lib/api/client", () => ({
  api: { GET: mocks.apiGet, POST: mocks.apiPost, PUT: vi.fn(), PATCH: vi.fn(), DELETE: vi.fn() },
}));

type JobOverrides = Record<string, unknown>;

function job(status: string, overrides: JobOverrides = {}) {
  return {
    id: JOB,
    requestedBy: { id: ME, fullName: "Synthetic User" },
    status,
    params: { subjectId: SUBJECT, topicId: TOPIC, grade: 10, type: "MultipleChoice", difficulty: "Recognition", count: 2, note: "**x**" },
    attempt: status === "Queued" ? 0 : 1,
    maxAttempts: 3,
    errorCode: null,
    errorMessage: null,
    inputTokens: 0,
    outputTokens: 0,
    duplicatesDropped: 0,
    questionIds: [],
    createdAt: new Date().toISOString(),
    ...overrides,
  };
}

function question(id: string, overrides: Record<string, unknown> = {}) {
  return {
    id,
    subjectId: SUBJECT,
    topicId: TOPIC,
    departmentId: DEPT,
    grade: 10,
    type: "MultipleChoice",
    difficulty: "Recognition",
    stem: `Stem ${id.slice(-2)} $x^2$`,
    payload: {
      options: [
        { label: "A", text: "a" },
        { label: "B", text: "b" },
        { label: "C", text: "c" },
        { label: "D", text: "d" },
      ],
      correctLabel: "B",
    },
    explanation: "Vì vậy",
    status: "Draft",
    revision: 1,
    source: "AI",
    aiJobId: JOB,
    aiOrdinal: 1,
    aiVerification: "Agree",
    aiVerificationNote: null,
    aiReviewedByAuthor: false,
    createdBy: { id: ME, fullName: "Synthetic User" },
    createdAt: "2026-11-23T08:00:00Z",
    updatedAt: "2026-11-23T08:00:00Z",
    ...overrides,
  };
}

const ok = (data: unknown) => Promise.resolve({ data, error: undefined, response: new Response(null, { status: 200 }) });
const notFound = () =>
  Promise.resolve({ data: undefined, error: { code: "NotFound", title: "x" }, response: new Response(null, { status: 404 }) });

function setupApi(jobResponses: Array<() => Promise<unknown>>, questions: Record<string, unknown> = {}) {
  let call = 0;
  mocks.apiGet.mockImplementation((path: string, init?: { params?: { path?: { id?: string } } }) => {
    switch (path) {
      case "/ai/jobs/{id}": {
        const next = jobResponses[Math.min(call, jobResponses.length - 1)];
        call += 1;
        return next();
      }
      case "/questions/{id}": {
        const q = questions[init?.params?.path?.id ?? ""];
        return q ? ok(q) : notFound();
      }
      case "/subjects":
        return ok({ items: [{ id: SUBJECT, name: "Vật lí", departmentId: DEPT }], page: 1, pageSize: 100, totalItems: 1, totalPages: 1 });
      case "/topics":
        return ok({ items: [{ id: TOPIC, subjectId: SUBJECT, grade: 10, name: "Động học", sortOrder: 1 }], page: 1, pageSize: 100, totalItems: 1, totalPages: 1 });
      default:
        throw new Error(`Unexpected GET ${path}`);
    }
  });
}

const jobCalls = () => mocks.apiGet.mock.calls.filter(([path]) => path === "/ai/jobs/{id}").length;

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <JobPage />
    </QueryClientProvider>,
  );
}

describe("AI job page", () => {
  beforeEach(() => {
    mocks.apiGet.mockReset();
    mocks.apiPost.mockReset();
    mocks.auth.me.id = ME;
  });
  afterEach(() => {
    cleanup();
    vi.useRealTimers();
  });

  it("polls with backoff and stops once the job succeeded", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    setupApi([() => ok(job("Queued")), () => ok(job("Running")), () => ok(job("Succeeded"))]);
    renderPage();
    expect(await screen.findByText(messages.ai.waiting)).toBeTruthy();
    expect(jobCalls()).toBe(1);

    await act(() => vi.advanceTimersByTimeAsync(2000));
    expect(await screen.findByText(messages.ai.running(1, 3))).toBeTruthy();
    expect(jobCalls()).toBe(2);

    await act(() => vi.advanceTimersByTimeAsync(4000));
    expect(await screen.findByText(messages.ai.allDuplicates)).toBeTruthy();
    expect(jobCalls()).toBe(3);

    await act(() => vi.advanceTimersByTimeAsync(60000));
    expect(jobCalls()).toBe(3);
  });

  it("shows retrying with the attempt and server message", async () => {
    setupApi([() => ok(job("Queued", { attempt: 1, errorMessage: "Dịch vụ AI đang quá tải, hệ thống sẽ thử lại." }))]);
    renderPage();
    expect(await screen.findByText(messages.ai.retrying(1, 3))).toBeTruthy();
    expect(screen.getByText("Dịch vụ AI đang quá tải, hệ thống sẽ thử lại.")).toBeTruthy();
  });

  it("hints that the system may be paused when queued for more than 15 minutes", async () => {
    setupApi([() => ok(job("Queued", { createdAt: "2020-01-01T00:00:00Z" }))]);
    renderPage();
    expect(await screen.findByText(messages.ai.staleHint)).toBeTruthy();
  });

  it("shows the paused hint once a job crosses 15 minutes while the page stays open", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const createdAt = new Date(Date.now() - (15 * 60 * 1000 - 10_000)).toISOString();
    setupApi([() => ok(job("Queued", { createdAt }))]);
    renderPage();
    expect(await screen.findByText(messages.ai.waiting)).toBeTruthy();
    expect(screen.queryByText(messages.ai.staleHint)).toBeNull();

    await act(() => vi.advanceTimersByTimeAsync(2000 + 4000 + 8000));
    expect(await screen.findByText(messages.ai.staleHint)).toBeTruthy();
  });

  it("shows the failure message and a regenerate link", async () => {
    setupApi([() => ok(job("Failed", { errorCode: "QuotaExceeded", errorMessage: "Đã vượt quota token hôm nay." }))]);
    renderPage();
    expect(await screen.findByText("Đã vượt quota token hôm nay.")).toBeTruthy();
    const link = screen.getByRole("link", { name: messages.ai.regenerate });
    expect(link.getAttribute("href")).toMatch(new RegExp(`^/ai/?\\?from=${JOB}$`));
  });

  it("shows an all-duplicates notice with a regenerate link when no question was kept", async () => {
    setupApi([() => ok(job("Succeeded", { duplicatesDropped: 2 }))]);
    renderPage();
    expect(await screen.findByText(messages.ai.allDuplicates)).toBeTruthy();
    expect(screen.getByRole("link", { name: messages.ai.regenerate })).toBeTruthy();
  });

  it("renders one card per question in order with the review summary", async () => {
    setupApi([() => ok(job("Succeeded", { questionIds: [Q1, Q2], duplicatesDropped: 1 }))], {
      [Q1]: question(Q1, { aiReviewedByAuthor: true }),
      [Q2]: question(Q2),
    });
    renderPage();
    expect(await screen.findByText(messages.ai.summary(2, 1, 1))).toBeTruthy();
    const headings = screen.getAllByText(/^Câu \d$/).map((el) => el.textContent);
    expect(headings).toEqual(["Câu 1", "Câu 2"]);
    expect(screen.getByText(/Stem b1/)).toBeTruthy();
    expect(screen.getByText(/Stem b2/)).toBeTruthy();
  });

  it("flags a Disagree verification with its note", async () => {
    setupApi([() => ok(job("Succeeded", { questionIds: [Q1] }))], {
      [Q1]: question(Q1, { aiVerification: "Disagree", aiVerificationNote: "Mô hình chọn C." }),
    });
    renderPage();
    expect(await screen.findByText(new RegExp(messages.ai.disagreeWarning))).toBeTruthy();
    expect(screen.getByText("Mô hình chọn C.")).toBeTruthy();
  });

  it("marks a question as reviewed and updates the counter", async () => {
    setupApi([() => ok(job("Succeeded", { questionIds: [Q1] }))], { [Q1]: question(Q1) });
    mocks.apiPost.mockResolvedValue({
      data: question(Q1, { aiReviewedByAuthor: true }),
      error: undefined,
      response: new Response(null, { status: 200 }),
    });
    renderPage();
    fireEvent.click(await screen.findByRole("button", { name: messages.questions.markReviewed }));
    await waitFor(() => expect(screen.getByText(messages.ai.summary(1, 0, 1))).toBeTruthy());
    expect(mocks.apiPost).toHaveBeenCalledWith("/questions/{id}/mark-reviewed", { params: { path: { id: Q1 } } });
    expect(screen.getByText(messages.ai.reviewed)).toBeTruthy();
  });

  it("shows the edited question when returning from the edit page within the app's 30s staleTime", async () => {
    let current: Record<string, unknown> = question(Q1, { aiReviewedByAuthor: true });
    setupApi([() => ok(job("Succeeded", { questionIds: [Q1] }))]);
    const base = mocks.apiGet.getMockImplementation()!;
    mocks.apiGet.mockImplementation((path: string, init?: unknown) =>
      path === "/questions/{id}" ? ok(current) : base(path, init),
    );
    // Same defaults as src/app/providers.tsx.
    const client = new QueryClient({ defaultOptions: { queries: { staleTime: 30_000, refetchOnWindowFocus: false, retry: false } } });
    const view = () => (
      <QueryClientProvider client={client}>
        <JobPage />
      </QueryClientProvider>
    );
    const first = render(view());
    expect(await screen.findByText(/Stem b1/)).toBeTruthy();
    expect(screen.getByText(messages.ai.reviewed)).toBeTruthy();
    first.unmount();

    // The author edits the question elsewhere; the server resets the review flag.
    current = question(Q1, { stem: "Đề đã sửa", aiReviewedByAuthor: false });
    render(view());
    expect(await screen.findByText("Đề đã sửa")).toBeTruthy();
    expect(screen.queryByText(messages.ai.reviewed)).toBeNull();
    expect(screen.getByRole("button", { name: messages.questions.markReviewed })).toBeTruthy();
  });

  it("writes mark-reviewed results to the shared question cache used by the detail page", async () => {
    setupApi([() => ok(job("Succeeded", { questionIds: [Q1] }))], { [Q1]: question(Q1) });
    mocks.apiPost.mockResolvedValue({
      data: question(Q1, { aiReviewedByAuthor: true }),
      error: undefined,
      response: new Response(null, { status: 200 }),
    });
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={client}>
        <JobPage />
      </QueryClientProvider>,
    );
    fireEvent.click(await screen.findByRole("button", { name: messages.questions.markReviewed }));
    await waitFor(() =>
      expect((client.getQueryData(["question", Q1]) as { aiReviewedByAuthor: boolean } | undefined)?.aiReviewedByAuthor).toBe(true),
    );
  });

  it("links each card to the question detail page for editing", async () => {
    setupApi([() => ok(job("Succeeded", { questionIds: [Q1] }))], { [Q1]: question(Q1) });
    renderPage();
    const edit = await screen.findByRole("link", { name: messages.common.edit });
    expect(edit.getAttribute("href")).toMatch(new RegExp(`^/questions/detail/?\\?id=${Q1}$`));
  });

  it("shows unavailable for a deleted question while other cards still render", async () => {
    setupApi([() => ok(job("Succeeded", { questionIds: [Q1, Q2] }))], { [Q2]: question(Q2) });
    renderPage();
    expect(await screen.findByText(messages.ai.questionUnavailable)).toBeTruthy();
    expect(await screen.findByText(/Stem b2/)).toBeTruthy();
    expect(screen.getByText(messages.ai.summary(1, 0, 0))).toBeTruthy();
  });

  it("shows a retryable error, not 'unavailable', when a question fails to load transiently", async () => {
    setupApi([() => ok(job("Succeeded", { questionIds: [Q1] }))]);
    const base = mocks.apiGet.getMockImplementation()!;
    let failures = 2; // the first load and its single automatic retry both fail
    mocks.apiGet.mockImplementation((path: string, init?: unknown) => {
      if (path === "/questions/{id}" && failures-- > 0) return Promise.reject(new TypeError("Failed to fetch"));
      if (path === "/questions/{id}") return ok(question(Q1));
      return base(path, init);
    });
    renderPage();
    expect(await screen.findByText(messages.ai.questionLoadFailed, undefined, { timeout: 4000 })).toBeTruthy();
    expect(screen.queryByText(messages.ai.questionUnavailable)).toBeNull();

    fireEvent.click(screen.getByRole("button", { name: messages.ai.retry }));
    expect(await screen.findByText(/Stem b1/)).toBeTruthy();
  });

  it("keeps the last job and shows a reconnecting notice when a poll fails", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    setupApi([() => ok(job("Queued")), () => Promise.reject(new TypeError("Failed to fetch"))]);
    renderPage();
    expect(await screen.findByText(messages.ai.waiting)).toBeTruthy();
    await act(() => vi.advanceTimersByTimeAsync(2000));
    expect(await screen.findByText(messages.ai.reconnecting)).toBeTruthy();
    expect(screen.getByText(messages.ai.waiting)).toBeTruthy();
  });

  it("shows not found for an unknown job", async () => {
    setupApi([notFound]);
    renderPage();
    expect(await screen.findByText(messages.ai.jobNotFound)).toBeTruthy();
  });

  it("never renders script or img elements from AI content but still renders KaTeX", async () => {
    setupApi([() => ok(job("Succeeded", { questionIds: [Q1] }))], {
      [Q1]: question(Q1, { stem: 'Xét <script>alert(1)</script><img src=x onerror="alert(1)"> và $x^2$' }),
    });
    const { container } = renderPage();
    await screen.findByText(/Xét/);
    expect(container.querySelector("script")).toBeNull();
    expect(container.querySelector("img")).toBeNull();
    expect(container.querySelector(".katex")).not.toBeNull();
  });

  it("renders the teacher note as plain text", async () => {
    setupApi([() => ok(job("Queued"))]);
    const { container } = renderPage();
    expect(await screen.findByText("**x**")).toBeTruthy();
    expect(container.querySelector("strong")).toBeNull();
  });
});

describe("canMarkReviewed", () => {
  const base = question(Q1) as Parameters<typeof canMarkReviewed>[0];

  it("is true for the author's unreviewed AI draft or rejected question", () => {
    expect(canMarkReviewed(base, ME)).toBe(true);
    expect(canMarkReviewed({ ...base, status: "Rejected" }, ME)).toBe(true);
  });

  it("is false for other authors, manual, reviewed, or non-editable statuses", () => {
    expect(canMarkReviewed(base, OTHER)).toBe(false);
    expect(canMarkReviewed({ ...base, source: "Manual" }, ME)).toBe(false);
    expect(canMarkReviewed({ ...base, aiReviewedByAuthor: true }, ME)).toBe(false);
    expect(canMarkReviewed({ ...base, status: "Submitted" }, ME)).toBe(false);
    expect(canMarkReviewed({ ...base, status: "Approved" }, ME)).toBe(false);
  });
});
