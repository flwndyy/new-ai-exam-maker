import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import HomePage from "@/app/page";
import { vi as messages } from "@/messages/vi";

const SUBJECT = "00000000-0000-0000-0000-000000000020";
const TOPIC = "00000000-0000-0000-0000-000000000030";

const mocks = vi.hoisted(() => ({
  auth: {
    status: "signedIn",
    me: {
      id: "00000000-0000-0000-0000-000000000001",
      email: "user@example.invalid",
      fullName: "Trần Thu Hà",
      role: "teacher" as "teacher" | "head" | "admin",
      departmentId: "00000000-0000-0000-0000-000000000010" as string | null,
      departmentName: "Tổ Toán" as string | null,
      isActive: true,
    },
  },
  apiGet: vi.fn(),
}));

vi.mock("next/navigation", () => ({
  useSearchParams: () => new URLSearchParams(""),
  useRouter: () => ({ push: vi.fn(), replace: vi.fn() }),
  usePathname: () => "/",
}));
vi.mock("@/lib/auth/auth-context", () => ({ useAuth: () => mocks.auth }));
vi.mock("@/lib/api/client", () => ({
  api: { GET: mocks.apiGet, POST: vi.fn(), PUT: vi.fn(), PATCH: vi.fn(), DELETE: vi.fn() },
}));

function ok(data: unknown) {
  return Promise.resolve({ data, error: undefined, response: new Response(null, { status: 200 }) });
}
function fail() {
  return Promise.resolve({
    data: undefined,
    error: { status: 500, title: "Server error" },
    response: new Response(null, { status: 500 }),
  });
}
function page(items: unknown[], totalItems = items.length) {
  return { items, page: 1, pageSize: 100, totalItems, totalPages: 1 };
}

const rejectedQuestion = {
  id: "Q1",
  subjectId: SUBJECT,
  topicId: TOPIC,
  grade: 10,
  stem: "Vật rơi tự do từ độ cao 45 m",
  status: "Rejected",
  source: "Manual",
  aiReviewedByAuthor: false,
  reviewNote: "Phương án B và C trùng nhau.",
};
const aiDraft = (id: string, jobId: string) => ({
  id,
  subjectId: SUBJECT,
  topicId: TOPIC,
  grade: 10,
  stem: "Câu AI",
  status: "Draft",
  source: "AI",
  aiJobId: jobId,
  aiReviewedByAuthor: false,
  createdAt: "2026-11-25T01:00:00Z",
});
const rejectedExam = {
  id: "E1",
  title: "Kiểm tra giữa kì",
  latestVersionId: "V3",
  latestVersionNo: 3,
  latestVersionStatus: "Rejected",
};
const runningJob = {
  id: "J1",
  status: "Running",
  params: { subjectId: SUBJECT, topicId: TOPIC, grade: 10, type: "MultipleChoice", difficulty: "Recognition", count: 5 },
  createdAt: "2026-11-25T02:00:00Z",
};
const reviewQuestion = {
  id: "RQ1",
  stem: "Câu chờ duyệt",
  type: "MultipleChoice",
  difficulty: "Recognition",
  source: "Manual",
  aiVerification: "NotChecked",
  createdBy: { id: "u2", fullName: "GV B" },
};
const reviewExam = {
  id: "RE1",
  examTitle: "Đề chờ duyệt",
  versionNo: 2,
  grade: 10,
  questionCount: 3,
  totalPoints: 10,
  createdBy: { id: "u2", fullName: "GV B" },
};

type Overrides = Partial<Record<string, () => Promise<unknown>>>;

function setupApi(overrides: Overrides = {}) {
  mocks.apiGet.mockImplementation((path: string, init?: { params?: { query?: { status?: string } } }) => {
    const key = path === "/questions" ? `${path}?${init?.params?.query?.status}` : path;
    if (overrides[key]) return overrides[key]!();
    switch (key) {
      case "/questions?Rejected":
        return ok(page([rejectedQuestion]));
      case "/questions?Draft":
        return ok(page([aiDraft("A1", "job-a"), aiDraft("A2", "job-a")]));
      case "/ai/jobs":
        return ok(page([runningJob]));
      case "/exams":
        return ok(page([rejectedExam]));
      case "/review/questions":
        return ok(page([reviewQuestion], 6));
      case "/review/exam-versions":
        return ok(page([reviewExam], 2));
      default:
        throw new Error(`Unexpected GET ${key}`);
    }
  });
}

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <HomePage />
    </QueryClientProvider>,
  );
}

const heading = (name: string) => screen.queryByRole("heading", { name });
const calledPaths = () => mocks.apiGet.mock.calls.map(([p]) => p as string);

describe("HomePage", () => {
  beforeEach(() => {
    mocks.auth.me.role = "teacher";
    mocks.auth.me.fullName = "Trần Thu Hà";
    mocks.auth.me.departmentName = "Tổ Toán";
    mocks.apiGet.mockReset();
    setupApi();
  });
  afterEach(cleanup);

  it("greets the user and shows the role line", async () => {
    renderPage();
    expect(screen.getByRole("heading", { level: 1, name: messages.home.greeting("Trần Thu Hà") })).toBeTruthy();
    expect(screen.getByText(messages.auth.roleLine("teacher", "Tổ Toán"))).toBeTruthy();
    await screen.findByText(rejectedQuestion.stem);
  });

  it("links the shortcuts of the role, without the home entry", async () => {
    renderPage();
    // next/link drops the trailing slash outside the real Next config
    const links = screen.getAllByRole("link").map((a) => (a.getAttribute("href") ?? "").replace(/\/$/, ""));
    expect(links).toEqual(expect.arrayContaining(["/questions", "/ai", "/exams"]));
    expect(links).not.toContain("/review");
    expect(screen.getByText(messages.home.shortcutDescriptions.ai)).toBeTruthy();
    await screen.findByText(rejectedQuestion.stem);
  });

  it("teacher sees own groups and no review group", async () => {
    renderPage();
    await screen.findByText(rejectedQuestion.stem);
    expect(heading(messages.home.rejectedQuestions)).toBeTruthy();
    expect(await screen.findByRole("heading", { name: messages.home.rejectedExams })).toBeTruthy();
    expect(screen.getByText(`${rejectedExam.title}, phiên bản v3`)).toBeTruthy();
    expect(screen.getByRole("link", { name: /Kiểm tra giữa kì/ }).getAttribute("href")).toMatch(/^\/exams\/view\/?\?id=E1&vid=V3$/);
    expect(await screen.findByText(messages.home.unreviewedCount(2))).toBeTruthy();
    expect(heading(messages.home.reviewQuestions)).toBeNull();
    expect(heading(messages.home.reviewExams)).toBeNull();
    expect(calledPaths()).not.toContain("/review/questions");
  });

  it("head also sees the review groups with their totals", async () => {
    mocks.auth.me.role = "head";
    renderPage();
    expect(await screen.findByRole("heading", { name: messages.home.reviewQuestions })).toBeTruthy();
    expect(await screen.findByRole("heading", { name: messages.home.reviewExams })).toBeTruthy();
    expect(heading(messages.home.rejectedQuestions)).toBeTruthy();
    expect(screen.getByText(messages.home.total(6))).toBeTruthy();
    const examsGroup = screen.getByRole("heading", { name: messages.home.reviewExams }).closest("section") as HTMLElement;
    const viewAll = within(examsGroup).getByRole("link", { name: messages.home.viewAll });
    expect(viewAll.getAttribute("href")).toMatch(/\/review\/?\?tab=exams/);
  });

  it("admin only queries the review groups", async () => {
    mocks.auth.me.role = "admin";
    mocks.auth.me.departmentName = null;
    renderPage();
    expect(await screen.findByRole("heading", { name: messages.home.reviewQuestions })).toBeTruthy();
    expect(await screen.findByRole("heading", { name: messages.home.reviewExams })).toBeTruthy();
    expect(heading(messages.home.rejectedQuestions)).toBeNull();
    expect(heading(messages.home.aiJobs)).toBeNull();
    const paths = calledPaths();
    expect(paths).not.toContain("/questions");
    expect(paths).not.toContain("/ai/jobs");
    expect(paths).not.toContain("/exams");
  });

  it("shows the empty message when every group is empty", async () => {
    setupApi({
      "/questions?Rejected": () => ok(page([])),
      "/questions?Draft": () => ok(page([])),
      "/ai/jobs": () => ok(page([])),
      "/exams": () => ok(page([])),
    });
    renderPage();
    expect(await screen.findByText(messages.home.nothingToDo)).toBeTruthy();
  });

  it("keeps other groups when one group fails, and retries it", async () => {
    let jobsFail = true;
    setupApi({ "/ai/jobs": () => (jobsFail ? fail() : ok(page([runningJob]))) });
    renderPage();
    expect(await screen.findByText(rejectedQuestion.stem)).toBeTruthy();
    const jobsHeading = await screen.findByRole("heading", { name: messages.home.aiJobs });
    const group = jobsHeading.closest("section") as HTMLElement;
    expect(await within(group).findByText(messages.home.loadFailed)).toBeTruthy();
    expect(screen.queryByText(messages.home.nothingToDo)).toBeNull();
    jobsFail = false;
    fireEvent.click(within(group).getByRole("button", { name: messages.home.retry }));
    await waitFor(() => expect(within(group).queryByText(messages.home.loadFailed)).toBeNull());
    expect(within(group).getAllByRole("link").length).toBeGreaterThan(0);
  });

  it("adds an at-least hint when more than 100 drafts exist", async () => {
    setupApi({ "/questions?Draft": () => ok(page([aiDraft("A1", "job-a")], 150)) });
    renderPage();
    expect(await screen.findByText(messages.home.unreviewedCount(1, true))).toBeTruthy();
  });

  it("falls back to a dated title when the catalog is not cached", async () => {
    renderPage();
    const row = await screen.findByText(messages.home.unreviewedCount(2));
    const group = row.closest("section") as HTMLElement;
    expect(group.textContent).toMatch(/Job sinh câu ngày/);
  });
});
