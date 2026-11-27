import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import AiPage from "@/app/ai/page";
import { vi as messages } from "@/messages/vi";

const SUBJECT = "00000000-0000-0000-0000-000000000020";
const TOPIC = "00000000-0000-0000-0000-000000000030";
const DEPT = "00000000-0000-0000-0000-000000000010";
const JOB = "00000000-0000-0000-0000-0000000000a1";

const mocks = vi.hoisted(() => ({
  auth: {
    status: "signedIn",
    me: {
      id: "00000000-0000-0000-0000-000000000001",
      email: "user@example.invalid",
      fullName: "Synthetic User",
      role: "teacher" as "teacher" | "head" | "admin",
      departmentId: "00000000-0000-0000-0000-000000000010" as string | null,
      isActive: true,
    },
  },
  search: "",
  push: vi.fn(),
  apiGet: vi.fn(),
  apiPost: vi.fn(),
}));

vi.mock("next/navigation", () => ({
  useSearchParams: () => new URLSearchParams(mocks.search),
  useRouter: () => ({ push: mocks.push, replace: vi.fn() }),
}));
vi.mock("@/lib/auth/auth-context", () => ({ useAuth: () => mocks.auth }));
vi.mock("@/lib/api/client", () => ({
  api: { GET: mocks.apiGet, POST: mocks.apiPost, PUT: vi.fn(), PATCH: vi.fn(), DELETE: vi.fn() },
}));

const quota = {
  date: "2026-11-23",
  jobsLimit: 10,
  jobsUsed: 3,
  tokensLimit: 100000,
  tokensUsed: 24500,
  maxQuestionsPerJob: 10,
  resetsAt: "2026-11-24T00:00:00Z",
};

function job(status: string) {
  return {
    id: JOB,
    requestedBy: { id: mocks.auth.me.id, fullName: "Synthetic User" },
    status,
    params: { subjectId: SUBJECT, topicId: TOPIC, grade: 11, type: "TrueFalse", difficulty: "Application", count: 4, note: "ôn tập" },
    attempt: 0,
    maxAttempts: 3,
    inputTokens: 0,
    outputTokens: 0,
    duplicatesDropped: 0,
    questionIds: [],
    createdAt: "2026-11-23T08:00:00Z",
  };
}

type Overrides = { quota?: object; jobs?: object[]; fromJob?: { status: number; body?: object } };

function ok(data: unknown) {
  return Promise.resolve({ data, error: undefined, response: new Response(null, { status: 200 }) });
}

function setupApi(o: Overrides = {}) {
  mocks.apiGet.mockImplementation((path: string, init?: { params?: { query?: Record<string, unknown> } }) => {
    switch (path) {
      case "/ai/quota":
        return ok(o.quota ?? quota);
      case "/subjects":
        return ok({ items: [{ id: SUBJECT, name: "Vật lí", departmentId: DEPT }], page: 1, pageSize: 100, totalItems: 1, totalPages: 1 });
      case "/topics": {
        const grade = init?.params?.query?.grade;
        return ok({
          items: [{ id: TOPIC, subjectId: SUBJECT, grade, name: "Động học", sortOrder: 1 }],
          page: 1,
          pageSize: 100,
          totalItems: 1,
          totalPages: 1,
        });
      }
      case "/ai/jobs": {
        const items = o.jobs ?? [];
        return ok({ items, page: 1, pageSize: 20, totalItems: items.length, totalPages: items.length ? 1 : 0 });
      }
      case "/ai/jobs/{id}":
        if (o.fromJob && o.fromJob.status !== 200) {
          return Promise.resolve({
            data: undefined,
            error: { code: "NotFound", title: "Không tìm thấy" },
            response: new Response(null, { status: o.fromJob.status }),
          });
        }
        return ok(o.fromJob?.body ?? job("Succeeded"));
      default:
        throw new Error(`Unexpected GET ${path}`);
    }
  });
}

function renderPage(ui: ReactNode = <AiPage />) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(<QueryClientProvider client={client}>{ui}</QueryClientProvider>);
}

async function fillValidForm() {
  await screen.findByRole("option", { name: "Vật lí" });
  fireEvent.change(screen.getByLabelText(messages.ai.subject), { target: { value: SUBJECT } });
  fireEvent.change(screen.getByLabelText(messages.ai.grade), { target: { value: "10" } });
  await screen.findByRole("option", { name: "Động học" });
  fireEvent.change(screen.getByLabelText(messages.ai.topic), { target: { value: TOPIC } });
}

describe("AI generation page", () => {
  beforeEach(() => {
    mocks.apiGet.mockReset();
    mocks.apiPost.mockReset();
    mocks.push.mockReset();
    mocks.search = "";
    mocks.auth.me.role = "teacher";
    mocks.auth.me.departmentId = DEPT;
  });
  afterEach(cleanup);

  it("shows how many jobs and tokens are left today, never negative", async () => {
    setupApi();
    const view = renderPage();
    const line = await screen.findByText(/^Hôm nay còn 7\/10 lượt/);
    expect(line.textContent).toMatch(/75\.500\/100\.000 token\. Làm mới lúc /);
    view.unmount();

    setupApi({ quota: { ...quota, jobsUsed: 12, tokensUsed: 120000 } });
    renderPage();
    const over = await screen.findByText(/^Hôm nay còn 0\/10 lượt/);
    expect(over.textContent).toMatch(/0\/100\.000 token\./);
  });

  it("shows the note warning without focusing the note field", async () => {
    setupApi();
    renderPage();
    const warning = await screen.findByText(messages.ai.noteWarning);
    const note = screen.getByLabelText(messages.ai.note);
    expect(warning.id).not.toBe("");
    expect(note.getAttribute("aria-describedby")).toContain(warning.id);
  });

  it("loads only subjects of the user's department", async () => {
    setupApi();
    renderPage();
    await screen.findByRole("option", { name: "Vật lí" });
    const call = mocks.apiGet.mock.calls.find(([path]) => path === "/subjects");
    expect(call?.[1]?.params?.query?.departmentId).toBe(DEPT);
  });

  it.each([
    ["teacher without department", "teacher", messages.ai.noDepartment],
    ["admin", "admin", messages.ai.forbidden],
  ] as const)("%s sees a no-permission message and no subject request", async (_label, role, text) => {
    setupApi();
    mocks.auth.me.role = role;
    mocks.auth.me.departmentId = null;
    renderPage();
    expect(await screen.findByText(text)).toBeTruthy();
    expect(mocks.apiGet.mock.calls.some(([path]) => path === "/subjects")).toBe(false);
    expect(screen.queryByLabelText(messages.ai.note)).toBeNull();
  });

  it("rejects a count above the quota maximum without posting", async () => {
    setupApi();
    renderPage();
    await fillValidForm();
    fireEvent.change(screen.getByLabelText(messages.ai.count), { target: { value: "11" } });
    fireEvent.click(screen.getByRole("button", { name: messages.ai.submit }));
    expect(await screen.findByText(messages.ai.validation.count(10))).toBeTruthy();
    expect(mocks.apiPost).not.toHaveBeenCalled();
  });

  it("submits and navigates to the job page", async () => {
    setupApi();
    mocks.apiPost.mockResolvedValue({ data: job("Queued"), error: undefined, response: new Response(null, { status: 202 }) });
    renderPage();
    await fillValidForm();
    fireEvent.change(screen.getByLabelText(messages.ai.note), { target: { value: "  tập trung đồ thị  " } });
    fireEvent.click(screen.getByRole("button", { name: messages.ai.submit }));
    await waitFor(() => expect(mocks.push).toHaveBeenCalledWith(`/ai/job/?id=${JOB}`));
    const [path, init] = mocks.apiPost.mock.calls[0];
    expect(path).toBe("/ai/generate");
    expect(init.body).toEqual({
      subjectId: SUBJECT,
      grade: 10,
      topicId: TOPIC,
      type: "MultipleChoice",
      difficulty: "Recognition",
      count: 5,
      note: "tập trung đồ thị",
    });
  });

  it.each([
    ["QuotaExceeded", null, messages.ai.quotaExceeded],
    ["RateLimited", "7", messages.ai.rateLimited(7)],
  ])("429 %s shows the matching message", async (code, retryAfter, text) => {
    setupApi();
    const headers: Record<string, string> = retryAfter ? { "Retry-After": retryAfter } : {};
    mocks.apiPost.mockResolvedValue({
      data: undefined,
      error: { code, title: "x" },
      response: new Response(null, { status: 429, headers }),
    });
    renderPage();
    await fillValidForm();
    fireEvent.click(screen.getByRole("button", { name: messages.ai.submit }));
    expect(await screen.findByText(text)).toBeTruthy();
    expect(mocks.push).not.toHaveBeenCalled();
  });

  it("shows a 400 subjectId error under the subject field", async () => {
    setupApi();
    mocks.apiPost.mockResolvedValue({
      data: undefined,
      error: { code: "ValidationFailed", title: "x", errors: { subjectId: ["Môn học không thuộc tổ chuyên môn của bạn."] } },
      response: new Response(null, { status: 400 }),
    });
    renderPage();
    await fillValidForm();
    fireEvent.click(screen.getByRole("button", { name: messages.ai.submit }));
    expect(await screen.findByText("Môn học không thuộc tổ chuyên môn của bạn.")).toBeTruthy();
  });

  it("disables submit when the daily job quota is used up", async () => {
    setupApi({ quota: { ...quota, jobsUsed: 10 } });
    renderPage();
    expect(await screen.findByText(messages.ai.quotaJobsExhausted)).toBeTruthy();
    expect((screen.getByRole("button", { name: messages.ai.submit }) as HTMLButtonElement).disabled).toBe(true);
  });

  it("reloads the quota at resetsAt and unlocks submit when the page stays open past the reset", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    try {
      const exhausted = { ...quota, jobsUsed: 10, resetsAt: new Date(Date.now() + 5000).toISOString() };
      const fresh = { ...quota, jobsUsed: 0, tokensUsed: 0, resetsAt: new Date(Date.now() + 86_400_000).toISOString() };
      let calls = 0;
      setupApi();
      const base = mocks.apiGet.getMockImplementation()!;
      mocks.apiGet.mockImplementation((path: string, init?: unknown) =>
        path === "/ai/quota" ? ok(calls++ === 0 ? exhausted : fresh) : base(path, init),
      );
      // Same defaults as src/app/providers.tsx (no refetch on focus, 30s staleTime).
      const client = new QueryClient({ defaultOptions: { queries: { staleTime: 30_000, refetchOnWindowFocus: false, retry: false } } });
      render(
        <QueryClientProvider client={client}>
          <AiPage />
        </QueryClientProvider>,
      );
      expect(await screen.findByText(messages.ai.quotaJobsExhausted)).toBeTruthy();

      await act(() => vi.advanceTimersByTimeAsync(7000));
      await waitFor(() => expect(screen.queryByText(messages.ai.quotaJobsExhausted)).toBeNull());
      expect((screen.getByRole("button", { name: messages.ai.submit }) as HTMLButtonElement).disabled).toBe(false);
    } finally {
      vi.useRealTimers();
    }
  });

  it("prefills the form from ?from=<jobId>", async () => {
    mocks.search = `from=${JOB}`;
    setupApi({ fromJob: { status: 200, body: job("Failed") } });
    renderPage();
    await waitFor(() => expect((screen.getByLabelText(messages.ai.note) as HTMLTextAreaElement).value).toBe("ôn tập"));
    expect((screen.getByLabelText(messages.ai.grade) as HTMLSelectElement).value).toBe("11");
    expect((screen.getByLabelText(messages.ai.type) as HTMLSelectElement).value).toBe("TrueFalse");
    expect((screen.getByLabelText(messages.ai.count) as HTMLInputElement).value).toBe("4");
    await waitFor(() => expect((screen.getByLabelText(messages.ai.topic) as HTMLSelectElement).value).toBe(TOPIC));
  });

  it("still renders a usable empty form when ?from=<jobId> is not found", async () => {
    mocks.search = `from=${JOB}`;
    setupApi({ fromJob: { status: 404 } });
    renderPage();
    expect(await screen.findByRole("button", { name: messages.ai.submit })).toBeTruthy();
    expect((screen.getByLabelText(messages.ai.note) as HTMLTextAreaElement).value).toBe("");
  });

  it("puts the quota line on the page header board", async () => {
    setupApi();
    const { container } = renderPage();
    const line = await screen.findByText(/^Hôm nay còn 7\/10 lượt/);
    expect(line.closest('[data-surface="board"]')).not.toBeNull();
    const resetsAt = new Intl.DateTimeFormat("vi-VN", {
      hour: "2-digit",
      minute: "2-digit",
      day: "2-digit",
      month: "2-digit",
      timeZone: "Asia/Ho_Chi_Minh",
    }).format(new Date(quota.resetsAt));
    expect(line.textContent).toBe(messages.ai.quota(7, 10, "75.500", "100.000", resetsAt));
    expect(container.querySelectorAll("form").length).toBe(1);
    expect(container.querySelector("form")?.textContent).not.toMatch(/^Hôm nay còn/);
  });

  it("lists my jobs as rows with a status tone and a link to the job page", async () => {
    setupApi({ jobs: [job("Running"), { ...job("Failed"), id: "00000000-0000-0000-0000-0000000000a2", errorMessage: "Đã hết token AI trong ngày." }] });
    const { container } = renderPage();
    expect(await screen.findByText(messages.ai.jobStatuses.Running)).toBeTruthy();
    const running = container.querySelector('[data-tone="wait"]');
    expect(running).not.toBeNull();
    const link = running!.querySelector("a");
    expect(link?.getAttribute("href")).toMatch(new RegExp(`^/ai/job/?\\?id=${JOB}$`));
    expect(running!.querySelector(".motion-safe\\:animate-pulse")?.getAttribute("aria-hidden")).toBe("true");
    const failed = container.querySelector('[data-tone="rej"]');
    expect(failed?.textContent).toContain("Đã hết token AI trong ngày.");
    expect(container.querySelector('[data-tone="ok"]')).toBeNull();
    expect(screen.queryByText(/Đã rà soát \d/)).toBeNull();
  });
});
