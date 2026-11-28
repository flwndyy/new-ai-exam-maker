import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import ExamsPage from "@/app/exams/page";
import { vi as messages } from "@/messages/vi";

const DEPT = "00000000-0000-0000-0000-000000000010";
const SUBJECT = "00000000-0000-0000-0000-000000000020";

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
  push: vi.fn(),
  apiGet: vi.fn(),
  apiPost: vi.fn(),
}));

vi.mock("next/navigation", () => ({
  useSearchParams: () => new URLSearchParams(""),
  useRouter: () => ({ push: mocks.push, replace: vi.fn() }),
}));
vi.mock("@/lib/auth/auth-context", () => ({ useAuth: () => mocks.auth }));
vi.mock("@/lib/api/client", () => ({
  api: { GET: mocks.apiGet, POST: mocks.apiPost, PUT: vi.fn(), PATCH: vi.fn(), DELETE: vi.fn() },
}));

function ok(data: unknown, status = 200) {
  return Promise.resolve({ data, error: undefined, response: new Response(null, { status }) });
}

const exam = {
  id: "E1",
  title: "Kiểm tra giữa kì",
  subjectId: SUBJECT,
  grade: 10,
  departmentId: DEPT,
  createdBy: { id: "u2", fullName: "Giáo viên B" },
  latestVersionNo: 3,
  latestVersionId: "V3",
  latestVersionStatus: "Rejected",
  publishedVersionId: "V2",
  publishedVersionNo: 2,
  createdAt: "2026-11-23T01:00:00Z",
  updatedAt: "2026-11-23T02:00:00Z",
};

function setupApi() {
  mocks.apiGet.mockImplementation((path: string) => {
    switch (path) {
      case "/exams":
        return ok({ items: [exam], page: 1, pageSize: 20, totalItems: 1, totalPages: 1 });
      case "/subjects":
        return ok({ items: [{ id: SUBJECT, name: "Hóa học", departmentId: DEPT }], page: 1, pageSize: 100, totalItems: 1, totalPages: 1 });
      default:
        throw new Error(`Unexpected GET ${path}`);
    }
  });
}

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <ExamsPage />
    </QueryClientProvider>,
  );
}

function lastExamsQuery() {
  const calls = mocks.apiGet.mock.calls.filter(([p]) => p === "/exams");
  return calls[calls.length - 1][1].params.query;
}

describe("ExamsPage", () => {
  beforeEach(() => {
    mocks.auth.me.role = "teacher";
    mocks.auth.me.departmentId = DEPT;
    setupApi();
  });
  afterEach(() => {
    cleanup();
    vi.clearAllMocks();
  });

  it("lists exams as rows with subject, author, latest version status and published version", async () => {
    renderPage();
    const link = await screen.findByRole("link", { name: exam.title });
    expect(link.getAttribute("href")).toMatch(/^\/exams\/view\/?\?id=E1$/);
    const row = link.closest("[data-tone]") as HTMLElement;
    expect(row.getAttribute("data-tone")).toBe("rej");
    expect(within(row).getByText(messages.exams.latestVersion(3, messages.review.examStatuses.Rejected))).toBeTruthy();
    expect(within(row).getByText(messages.exams.publishedVersion(2))).toBeTruthy();
    await waitFor(() =>
      expect(within(row).getByText(/^Hóa học, khối 10\. Người tạo Giáo viên B\. Cập nhật/)).toBeTruthy(),
    );
  });

  it("shows no published line when no version is published", async () => {
    mocks.apiGet.mockImplementation((path: string) =>
      path === "/exams"
        ? ok({
            items: [{ ...exam, latestVersionStatus: "Approved", publishedVersionId: null, publishedVersionNo: null }],
            page: 1,
            pageSize: 20,
            totalItems: 1,
            totalPages: 1,
          })
        : ok({ items: [], page: 1, pageSize: 100, totalItems: 0, totalPages: 0 }),
    );
    renderPage();
    const row = (await screen.findByRole("link", { name: exam.title })).closest("[data-tone]") as HTMLElement;
    expect(row.getAttribute("data-tone")).toBe("ok");
    expect(screen.queryByText(/Đang xuất bản/)).toBeNull();
  });

  it("renders the page title on the board", async () => {
    renderPage();
    await screen.findByRole("link", { name: exam.title });
    expect(screen.getByRole("heading", { level: 1, name: messages.exams.listTitle })).toBeTruthy();
  });

  it("admin has no create button", async () => {
    mocks.auth.me.role = "admin";
    mocks.auth.me.departmentId = null;
    renderPage();
    await screen.findByRole("link", { name: exam.title });
    expect(screen.queryByRole("button", { name: messages.exams.create })).toBeNull();
  });

  it("mine-only filter re-queries with mine=true", async () => {
    renderPage();
    await screen.findByRole("link", { name: exam.title });
    fireEvent.click(screen.getByLabelText(messages.exams.mineOnly));
    await waitFor(() => expect(lastExamsQuery().mine).toBe(true));
    expect(lastExamsQuery().pageSize).toBe(20);
  });

  it("creates an exam and opens its first version", async () => {
    mocks.apiPost.mockReturnValue(ok({ ...exam, id: "E9", versions: [{ id: "V9" }] }, 201));
    renderPage();
    fireEvent.click(await screen.findByRole("button", { name: messages.exams.create }));
    const form = within(screen.getByRole("form", { name: messages.exams.createTitle }));
    fireEvent.change(form.getByLabelText(messages.exams.titleLabel), { target: { value: "  Kiểm tra 15 phút " } });
    await form.findByRole("option", { name: "Hóa học" });
    fireEvent.change(form.getByLabelText(messages.exams.subject), { target: { value: SUBJECT } });
    fireEvent.change(form.getByLabelText(messages.exams.grade), { target: { value: "10" } });
    fireEvent.click(form.getByRole("button", { name: messages.exams.createTitle }));
    await waitFor(() =>
      expect(mocks.apiPost).toHaveBeenCalledWith("/exams", { body: { title: "Kiểm tra 15 phút", subjectId: SUBJECT, grade: 10 } }),
    );
    await waitFor(() => expect(mocks.push).toHaveBeenCalledWith("/exams/view/?id=E9&vid=V9"));
  });

  it("shows API field errors next to the field", async () => {
    mocks.apiPost.mockReturnValue(
      Promise.resolve({ data: undefined, error: { code: "ValidationFailed", errors: { title: ["Trùng"] } }, response: new Response(null, { status: 400 }) }),
    );
    renderPage();
    fireEvent.click(await screen.findByRole("button", { name: messages.exams.create }));
    const form = within(screen.getByRole("form", { name: messages.exams.createTitle }));
    fireEvent.change(form.getByLabelText(messages.exams.titleLabel), { target: { value: "Đề" } });
    await form.findByRole("option", { name: "Hóa học" });
    fireEvent.change(form.getByLabelText(messages.exams.subject), { target: { value: SUBJECT } });
    fireEvent.change(form.getByLabelText(messages.exams.grade), { target: { value: "11" } });
    fireEvent.click(form.getByRole("button", { name: messages.exams.createTitle }));
    expect(await screen.findByText("Trùng")).toBeTruthy();
    expect(mocks.push).not.toHaveBeenCalled();
  });
});
