import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import ExamViewPage from "@/app/exams/view/page";
import { vi as messages } from "@/messages/vi";

const ME = "00000000-0000-0000-0000-000000000001";
const OTHER = "00000000-0000-0000-0000-000000000002";
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
  search: "",
  replace: vi.fn(),
  apiGet: vi.fn(),
  apiPost: vi.fn(),
  apiPut: vi.fn(),
}));

vi.mock("next/navigation", () => ({
  useSearchParams: () => new URLSearchParams(mocks.search),
  useRouter: () => ({ push: vi.fn(), replace: mocks.replace }),
}));
vi.mock("@/lib/auth/auth-context", () => ({ useAuth: () => mocks.auth }));
vi.mock("@/lib/api/client", () => ({
  api: { GET: mocks.apiGet, POST: mocks.apiPost, PUT: mocks.apiPut, PATCH: vi.fn(), DELETE: vi.fn() },
}));

function ok(data: unknown, status = 200) {
  return Promise.resolve({ data, error: undefined, response: new Response(null, { status }) });
}
function fail(status: number, error: object) {
  return Promise.resolve({ data: undefined, error, response: new Response(null, { status }) });
}

function summary(id: string, versionNo: number, status: string, createdBy = ME) {
  return {
    id,
    examId: "E1",
    examTitle: "Đề Hóa",
    subjectId: SUBJECT,
    grade: 10,
    departmentId: DEPT,
    createdBy: { id: createdBy, fullName: "Tác giả" },
    versionNo,
    status,
    durationMinutes: 45,
    totalPoints: 2,
    questionCount: 2,
    contentHash: null,
    submittedAt: null,
    reviewedBy: null,
    reviewedAt: null,
    reviewNote: status === "Rejected" ? "Câu 2 sai" : null,
    publishedBy: null,
    publishedAt: null,
    createdAt: "2026-11-23T00:00:00Z",
    updatedAt: "2026-11-23T00:00:00Z",
  };
}

function versionItem(id: string, stem: string | null, sourceRevision: number, currentSourceRevision: number | null) {
  return {
    sortOrder: 1,
    points: 1,
    sourceQuestionId: id,
    sourceRevision,
    currentSourceRevision,
    type: stem ? "Essay" : null,
    difficulty: stem ? "Recognition" : null,
    stem,
    payload: stem ? { modelAnswer: "m", rubric: [{ criterion: "c", points: 1 }] } : null,
    explanation: null,
  };
}

function examDetail(createdBy: string, versions: object[]) {
  return {
    id: "E1",
    title: "Đề Hóa",
    subjectId: SUBJECT,
    grade: 10,
    departmentId: DEPT,
    createdBy: { id: createdBy, fullName: "Tác giả" },
    latestVersionNo: 2,
    publishedVersionId: null,
    createdAt: "2026-11-23T00:00:00Z",
    updatedAt: "2026-11-23T00:00:00Z",
    versions,
  };
}

type Setup = { exam: object; versions: Record<string, { status: number; body: object }> };

function setupApi(s: Setup) {
  mocks.apiGet.mockImplementation((path: string, init?: { params?: { path?: Record<string, string> } }) => {
    if (path === "/exams/{id}") return ok(s.exam);
    if (path === "/exam-versions/{vid}") {
      const r = s.versions[init?.params?.path?.vid ?? ""];
      if (!r) throw new Error(`Unexpected version ${init?.params?.path?.vid}`);
      return r.status === 200 ? ok(r.body) : fail(r.status, r.body);
    }
    if (path === "/questions") return ok({ items: [], page: 1, pageSize: 20, totalItems: 0, totalPages: 0 });
    if (path === "/topics") return ok({ items: [], page: 1, pageSize: 100, totalItems: 0, totalPages: 0 });
    throw new Error(`Unexpected GET ${path}`);
  });
}

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <ExamViewPage />
    </QueryClientProvider>,
  );
}

function requestedVids() {
  return mocks.apiGet.mock.calls.filter(([p]) => p === "/exam-versions/{vid}").map(([, init]) => init.params.path.vid);
}

describe("ExamViewPage", () => {
  beforeEach(() => {
    mocks.auth.me.role = "teacher";
    mocks.auth.me.id = ME;
    mocks.search = "id=E1";
  });
  afterEach(() => {
    cleanup();
    vi.clearAllMocks();
  });

  it("asks for an id when missing", () => {
    mocks.search = "";
    renderPage();
    expect(screen.getByText(messages.exams.missingId)).toBeTruthy();
  });

  it("opens the newest editable version when no vid is given", async () => {
    const v2 = summary("V2", 2, "Draft");
    setupApi({
      exam: examDetail(ME, [v2, summary("V1", 1, "Rejected")]),
      versions: { V2: { status: 200, body: { ...v2, items: [], warnings: [] } } },
    });
    renderPage();
    await waitFor(() => expect(requestedVids()).toContain("V2"));
    expect(requestedVids()).not.toContain("V1");
  });

  it("pins the resolved version in the URL so later status changes cannot switch it", async () => {
    const v2 = summary("V2", 2, "Draft");
    setupApi({
      exam: examDetail(ME, [v2, summary("V1", 1, "Rejected")]),
      versions: { V2: { status: 200, body: { ...v2, items: [], warnings: [] } } },
    });
    renderPage();
    await waitFor(() => expect(mocks.replace).toHaveBeenCalledWith("/exams/view/?id=E1&vid=V2"));
  });

  it("shows the student-sheet notice and no edit controls on 403", async () => {
    setupApi({
      exam: examDetail(OTHER, [summary("V1", 1, "Published", OTHER)]),
      versions: { V1: { status: 403, body: { code: "Forbidden", title: "Forbidden" } } },
    });
    renderPage();
    expect(await screen.findByText(messages.exams.studentSheetOnly)).toBeTruthy();
    expect(screen.queryByRole("button", { name: messages.exams.save })).toBeNull();
    expect(screen.queryByRole("button", { name: messages.exams.submit })).toBeNull();
    expect(screen.queryByRole("button", { name: messages.exams.editTitle })).toBeNull();
  });

  it("shows both export links for an exportable version the user can read", async () => {
    const v1 = summary("V1", 1, "Approved");
    setupApi({ exam: examDetail(ME, [v1]), versions: { V1: { status: 200, body: { ...v1, items: [], warnings: [] } } } });
    mocks.search = "id=E1&vid=V1";
    renderPage();
    const student = await screen.findByRole("link", { name: messages.exams.print.toStudent });
    expect(student.getAttribute("href")).toMatch(/^\/exams\/print\/?\?id=E1&vid=V1&mode=student$/);
    expect(screen.getByRole("link", { name: messages.exams.print.toAnswer }).getAttribute("href")).toMatch(
      /^\/exams\/print\/?\?id=E1&vid=V1&mode=answer$/,
    );
  });

  it.each(["Draft", "Rejected", "Archived"])("shows no export links for %s", async (status) => {
    const v1 = summary("V1", 1, status);
    setupApi({ exam: examDetail(ME, [v1]), versions: { V1: { status: 200, body: { ...v1, items: [], warnings: [] } } } });
    mocks.search = "id=E1&vid=V1";
    renderPage();
    await waitFor(() => expect(requestedVids()).toContain("V1"));
    await waitFor(() => expect(screen.queryByText(messages.common.loading)).toBeNull());
    expect(screen.queryByRole("link", { name: messages.exams.print.toStudent })).toBeNull();
    expect(screen.queryByRole("link", { name: messages.exams.print.toAnswer })).toBeNull();
  });

  it("on 403 shows the student-sheet notice with a link to mode=student and no answer link", async () => {
    setupApi({
      exam: examDetail(OTHER, [summary("V1", 1, "Published", OTHER)]),
      versions: { V1: { status: 403, body: { code: "Forbidden", title: "Forbidden" } } },
    });
    mocks.search = "id=E1&vid=V1";
    renderPage();
    expect(await screen.findByText(messages.exams.studentSheetOnly)).toBeTruthy();
    expect(screen.getByRole("link", { name: messages.exams.print.toStudent }).getAttribute("href")).toMatch(
      /^\/exams\/print\/?\?id=E1&vid=V1&mode=student$/,
    );
    expect(screen.queryByRole("link", { name: messages.exams.print.toAnswer })).toBeNull();
  });

  it("read-only view flags drifted sources and unavailable content", async () => {
    mocks.auth.me.role = "head";
    const v1 = summary("V1", 1, "Draft", OTHER);
    setupApi({
      exam: examDetail(OTHER, [v1]),
      versions: {
        V1: {
          status: 200,
          body: {
            ...v1,
            items: [versionItem("q1", "Câu đổi", 2, 3), versionItem("q2", "Câu giữ", 1, 1), versionItem("q3", null, 1, null)],
            warnings: [{ code: "SourceContentUnavailable", message: "Một câu không xem được.", sourceQuestionId: "q3" }],
          },
        },
      },
    });
    renderPage();
    const changed = within((await screen.findByText("Câu đổi")).closest("li") as HTMLElement);
    expect(changed.getByText(messages.exams.revisionDrift)).toBeTruthy();
    const kept = within(screen.getByText("Câu giữ").closest("li") as HTMLElement);
    expect(kept.queryByText(messages.exams.revisionDrift)).toBeNull();
    expect(screen.getByText(messages.exams.contentUnavailable)).toBeTruthy();
    expect(screen.getByText("Một câu không xem được.")).toBeTruthy();
    expect(screen.queryByRole("button", { name: messages.exams.save })).toBeNull();
  });

  it("author edits the exam title", async () => {
    const v1 = summary("V1", 1, "Approved");
    setupApi({ exam: examDetail(ME, [v1]), versions: { V1: { status: 200, body: { ...v1, items: [], warnings: [] } } } });
    mocks.apiPut.mockReturnValue(ok(examDetail(ME, [v1])));
    renderPage();
    fireEvent.click(await screen.findByRole("button", { name: messages.exams.editTitle }));
    fireEvent.change(screen.getByLabelText(messages.exams.titleLabel), { target: { value: "Mới" } });
    fireEvent.click(screen.getByRole("button", { name: messages.exams.saveTitle }));
    await waitFor(() =>
      expect(mocks.apiPut).toHaveBeenCalledWith("/exams/{id}", { params: { path: { id: "E1" } }, body: { title: "Mới" } }),
    );
  });

  it("asks before switching version with unsaved composer changes", async () => {
    const v2 = summary("V2", 2, "Draft");
    const v1 = summary("V1", 1, "Approved");
    setupApi({
      exam: examDetail(ME, [v2, v1]),
      versions: { V2: { status: 200, body: { ...v2, items: [versionItem("q1", "Câu một", 1, 1)], warnings: [] } } },
    });
    const confirm = vi.spyOn(window, "confirm").mockReturnValue(false);
    mocks.search = "id=E1&vid=V2";
    renderPage();
    const points = await screen.findByLabelText(`${messages.exams.pointsLabel} 1`);
    fireEvent.change(points, { target: { value: "3" } });
    await screen.findByRole("button", { name: messages.exams.save });
    fireEvent.change(screen.getByLabelText(messages.exams.versionLabel), { target: { value: "V1" } });
    expect(confirm).toHaveBeenCalledWith(messages.exams.confirmDiscard);
    expect(mocks.replace).not.toHaveBeenCalled();
    confirm.mockRestore();
  });

  it("switching version replaces the vid in the URL", async () => {
    const v2 = summary("V2", 2, "Approved");
    const v1 = summary("V1", 1, "Rejected");
    setupApi({
      exam: examDetail(OTHER, [v2, v1]),
      versions: { V1: { status: 200, body: { ...v1, items: [], warnings: [] } }, V2: { status: 200, body: { ...v2, items: [], warnings: [] } } },
    });
    mocks.auth.me.role = "head";
    renderPage();
    const select = await screen.findByLabelText(messages.exams.versionLabel);
    fireEvent.change(select, { target: { value: "V2" } });
    expect(mocks.replace).toHaveBeenCalledWith("/exams/view/?id=E1&vid=V2");
  });
});
