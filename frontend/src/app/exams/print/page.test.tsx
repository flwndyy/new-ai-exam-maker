import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import ExamPrintPage from "@/app/exams/print/page";
import { vi as messages } from "@/messages/vi";

const mocks = vi.hoisted(() => ({
  search: "",
  apiGet: vi.fn(),
}));

vi.mock("next/navigation", () => ({
  useSearchParams: () => new URLSearchParams(mocks.search),
  useRouter: () => ({ push: vi.fn(), replace: vi.fn() }),
}));
vi.mock("@/lib/auth/auth-context", () => ({
  useAuth: () => ({
    status: "signedIn",
    me: { id: "U1", email: "user@example.invalid", fullName: "Synthetic", role: "teacher", departmentId: "D1", isActive: true },
  }),
}));
vi.mock("@/lib/api/client", () => ({
  api: { GET: mocks.apiGet, POST: vi.fn(), PUT: vi.fn(), PATCH: vi.fn(), DELETE: vi.fn() },
}));

function ok(data: unknown) {
  return Promise.resolve({ data, error: undefined, response: new Response(null, { status: 200 }) });
}
function fail(status: number) {
  return Promise.resolve({ data: undefined, error: { code: "X" }, response: new Response(null, { status }) });
}

const header = {
  examTitle: "Kiểm tra 15 phút",
  subjectName: "Toán",
  departmentName: "Tổ Toán",
  grade: 10,
  versionNo: 3,
  durationMinutes: 15,
  totalPoints: 10,
};

// Simulates a buggy server that leaks answer fields: the student view must still never render them.
const leakyStudentSheet = {
  header,
  items: [
    {
      number: 1,
      points: 2.5,
      type: "MultipleChoice",
      stem: "Câu trắc nghiệm",
      payload: {
        options: [
          { label: "A", text: "Một" },
          { label: "B", text: "Hai" },
          { label: "C", text: "Ba" },
          { label: "D", text: "Bốn" },
        ],
        correctLabel: "B",
      },
      explanation: "LEAK-SENTINEL",
    },
    { number: 2, points: 2.5, type: "TrueFalse", stem: "Câu đúng sai", payload: { statements: [{ label: "a", text: "Ý a", isTrue: true }] } },
    { number: 3, points: 2.5, type: "ShortAnswer", stem: "Câu ngắn", payload: { answerType: "number", unit: "m/s", acceptedAnswers: ["LEAK-ANSWER"] } },
    { number: 4, points: 2.5, type: "Essay", stem: "Câu tự luận", payload: { modelAnswer: "LEAK-MODEL" } },
  ],
};

const answerKey = {
  header,
  items: [
    {
      number: 1,
      points: 2.5,
      type: "MultipleChoice",
      difficulty: "Recognition",
      stem: "Câu trắc nghiệm",
      payload: {
        options: [
          { label: "A", text: "Một" },
          { label: "B", text: "Hai" },
          { label: "C", text: "Ba" },
          { label: "D", text: "Bốn" },
        ],
        correctLabel: "B",
      },
      explanation: "Lời giải mẫu",
    },
    {
      number: 2,
      points: 2.5,
      type: "TrueFalse",
      difficulty: "Understanding",
      stem: "Câu đúng sai",
      payload: { statements: [{ label: "a", text: "Ý a", isTrue: true }] },
      explanation: null,
    },
    {
      number: 3,
      points: 2.5,
      type: "ShortAnswer",
      difficulty: "Application",
      stem: "Câu ngắn",
      payload: { answerType: "number", acceptedAnswers: ["12"], unit: "m/s", tolerance: 0.5 },
      explanation: null,
    },
    {
      number: 4,
      points: 2.5,
      type: "Essay",
      difficulty: "HighApplication",
      stem: "Câu tự luận",
      payload: { modelAnswer: "Bài mẫu", rubric: [{ criterion: "Tiêu chí 1", points: 2.5 }] },
      explanation: null,
    },
  ],
};

// Same defaults as src/app/providers.tsx so cache behaviour matches the real app.
function appClient() {
  return new QueryClient({ defaultOptions: { queries: { staleTime: 30_000, refetchOnWindowFocus: false, retry: false } } });
}

function renderPage(search: string, client = appClient()) {
  mocks.search = search;
  return render(
    <QueryClientProvider client={client}>
      <ExamPrintPage />
    </QueryClientProvider>,
  );
}

describe("ExamPrintPage", () => {
  beforeEach(() => {
    mocks.apiGet.mockReset();
  });
  afterEach(() => cleanup());

  it("student mode calls only student-sheet and never renders answer data even if the response carries it", async () => {
    mocks.apiGet.mockImplementation((path: string) => (path === "/exam-versions/{vid}/student-sheet" ? ok(leakyStudentSheet) : fail(500)));
    renderPage("id=E1&vid=V1&mode=student");

    expect(await screen.findByText("Kiểm tra 15 phút")).toBeTruthy();
    expect(mocks.apiGet.mock.calls.map((c) => c[0])).toEqual(["/exam-versions/{vid}/student-sheet"]);
    expect(screen.getByText(messages.exams.print.studentTitle)).toBeTruthy();
    expect(screen.getByText(new RegExp(messages.exams.print.studentName))).toBeTruthy();
    expect(screen.getByText(/Câu 1/)).toBeTruthy();
    expect(screen.getByText("Hai")).toBeTruthy();
    expect(screen.getByText(/m\/s/)).toBeTruthy();
    expect(screen.queryByText(/LEAK/)).toBeNull();
    expect(screen.queryByText(new RegExp(`${messages.exams.print.answerLabel}:`))).toBeNull();
    expect(screen.queryByText(messages.exams.print.explanation)).toBeNull();
    expect(screen.queryByText(messages.questions.difficulties.Recognition)).toBeNull();
  });

  it("answer mode calls only answer-key and shows correct label, true/false values, accepted answers, rubric and explanation", async () => {
    mocks.apiGet.mockImplementation((path: string) => (path === "/exam-versions/{vid}/answer-key" ? ok(answerKey) : fail(500)));
    renderPage("id=E1&vid=V1&mode=answer");

    expect(await screen.findByText(messages.exams.print.answerTitle)).toBeTruthy();
    expect(mocks.apiGet.mock.calls.map((c) => c[0])).toEqual(["/exam-versions/{vid}/answer-key"]);
    expect(screen.getByText(new RegExp(`${messages.exams.print.answerLabel}: B`))).toBeTruthy();
    expect(screen.getByText(messages.questions.trueLabel)).toBeTruthy();
    expect(screen.getByText(/12/)).toBeTruthy();
    expect(screen.getByText("Tiêu chí 1")).toBeTruthy();
    expect(screen.getByText("Lời giải mẫu")).toBeTruthy();
    expect(screen.getByText(messages.questions.difficulties.Recognition)).toBeTruthy();
  });

  // The browser uses document.title as the default PDF name: the two files must never share a name.
  it.each([
    ["student", "/exam-versions/{vid}/student-sheet", leakyStudentSheet, messages.exams.print.docStudent],
    ["answer", "/exam-versions/{vid}/answer-key", answerKey, messages.exams.print.docAnswer],
  ] as const)("names the %s document after the exam, version code and mode", async (mode, path, body, label) => {
    document.title = "App";
    mocks.apiGet.mockImplementation((p: string) => (p === path ? ok(body) : fail(500)));
    const view = renderPage(`id=E1&vid=V1&mode=${mode}`);
    await screen.findByText("Kiểm tra 15 phút");
    await waitFor(() => expect(document.title).toBe(`Kiểm tra 15 phút - ${messages.exams.print.versionCode} 3 - ${label}`));
    view.unmount();
    expect(document.title).toBe("App");
  });

  it.each(["student", "answer"] as const)(
    "%s mode reloads on every open instead of printing a cached snapshot from before a resubmit",
    async (mode) => {
      const client = appClient();
      const body = mode === "student" ? leakyStudentSheet : answerKey;
      const resubmitted = { ...body, items: body.items.map((i) => ({ ...i, stem: `${i.stem} (mới)` })) };
      mocks.apiGet.mockImplementation(() => ok(body));
      const first = renderPage(`id=E1&vid=V1&mode=${mode}`, client);
      expect(await screen.findByText("Câu trắc nghiệm")).toBeTruthy();
      first.unmount();

      // Withdraw → edit → resubmit happened elsewhere; same vid, new snapshot, reopened within staleTime.
      mocks.apiGet.mockImplementation(() => ok(resubmitted));
      renderPage(`id=E1&vid=V1&mode=${mode}`, client);
      expect(screen.queryByText("Câu trắc nghiệm")).toBeNull();
      expect(await screen.findByText("Câu trắc nghiệm (mới)")).toBeTruthy();
      expect(mocks.apiGet).toHaveBeenCalledTimes(2);
    },
  );

  it("hides previously loaded content and the print button when a refetch is refused", async () => {
    const client = appClient();
    mocks.apiGet.mockImplementation(() => ok(leakyStudentSheet));
    renderPage("id=E1&vid=V1&mode=student", client);
    expect(await screen.findByText("Câu trắc nghiệm")).toBeTruthy();

    mocks.apiGet.mockImplementation(() => fail(409));
    await client.refetchQueries({ queryKey: ["examExport"] });
    expect(await screen.findByText(messages.exams.print.unavailable)).toBeTruthy();
    expect(screen.queryByText("Câu trắc nghiệm")).toBeNull();
    expect(screen.queryByText("Kiểm tra 15 phút")).toBeNull();
    expect(screen.queryByRole("button", { name: messages.exams.print.printButton })).toBeNull();
  });

  it.each(["vid=V1&mode=foo", "mode=student", "id=E1"])("shows the invalid-link message and makes no API call for %s", (search) => {
    renderPage(search);
    expect(screen.getByText(messages.exams.print.invalidLink)).toBeTruthy();
    expect(mocks.apiGet).not.toHaveBeenCalled();
  });

  it.each([
    [404, "student", "notFound"],
    [403, "answer", "answerForbidden"],
    [409, "student", "unavailable"],
  ] as const)("maps %i in %s mode to messages.exams.print.%s", async (status, mode, key) => {
    mocks.apiGet.mockImplementation(() => fail(status));
    renderPage(`id=E1&vid=V1&mode=${mode}`);
    expect(await screen.findByText(messages.exams.print[key])).toBeTruthy();
  });

  it("print button calls window.print and links point to the other mode and back to the exam", async () => {
    const print = vi.spyOn(window, "print").mockImplementation(() => {});
    mocks.apiGet.mockImplementation(() => ok(leakyStudentSheet));
    renderPage("id=E1&vid=V1&mode=student");
    fireEvent.click(await screen.findByRole("button", { name: messages.exams.print.printButton }));
    expect(print).toHaveBeenCalledOnce();
    // next/link drops the trailing slash in tests; the build adds it back via trailingSlash: true.
    expect(screen.getByRole("link", { name: messages.exams.print.toAnswer }).getAttribute("href")).toMatch(/^\/exams\/print\/?\?id=E1&vid=V1&mode=answer$/);
    expect(screen.getByRole("link", { name: messages.exams.print.back }).getAttribute("href")).toMatch(/^\/exams\/view\/?\?id=E1&vid=V1$/);
    print.mockRestore();
  });
});
