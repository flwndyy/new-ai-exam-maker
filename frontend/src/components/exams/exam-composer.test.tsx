import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { ExamComposer } from "@/components/exams/exam-composer";
import type { components } from "@/lib/api/schema";
import { vi as messages } from "@/messages/vi";

type ExamDetail = components["schemas"]["ExamDetail"];
type ExamVersion = components["schemas"]["ExamVersion"];

const ME = "00000000-0000-0000-0000-000000000001";
const SUBJECT = "00000000-0000-0000-0000-000000000020";

const mocks = vi.hoisted(() => ({ apiGet: vi.fn(), apiPut: vi.fn() }));
vi.mock("@/lib/api/client", () => ({
  api: { GET: mocks.apiGet, POST: vi.fn(), PUT: mocks.apiPut, PATCH: vi.fn(), DELETE: vi.fn() },
}));

function ok(data: unknown) {
  return Promise.resolve({ data, error: undefined, response: new Response(null, { status: 200 }) });
}
function fail(status: number, error: object) {
  return Promise.resolve({ data: undefined, error, response: new Response(null, { status }) });
}

function item(id: string, stem: string | null, points: number) {
  return {
    sortOrder: 1,
    points,
    sourceQuestionId: id,
    sourceRevision: 1,
    currentSourceRevision: stem ? 1 : null,
    type: stem ? "Essay" : null,
    difficulty: stem ? "Recognition" : null,
    stem,
    payload: stem ? { modelAnswer: "m", rubric: [{ criterion: "c", points: 1 }] } : null,
    explanation: null,
  };
}

function version(items: object[]): ExamVersion {
  return {
    id: "V1",
    examId: "E1",
    examTitle: "Đề",
    subjectId: SUBJECT,
    grade: 10,
    departmentId: "d",
    createdBy: { id: ME, fullName: "Tôi" },
    versionNo: 1,
    status: "Draft",
    durationMinutes: 45,
    totalPoints: 0,
    questionCount: items.length,
    contentHash: null,
    createdAt: "2026-11-23T00:00:00Z",
    updatedAt: "2026-11-23T00:00:00Z",
    items,
    warnings: [],
  } as unknown as ExamVersion;
}

const exam = { id: "E1", subjectId: SUBJECT, grade: 10, createdBy: { id: ME, fullName: "Tôi" }, versions: [] } as unknown as ExamDetail;

const bankQuestion = {
  id: "q2",
  subjectId: SUBJECT,
  topicId: "t",
  departmentId: "d",
  grade: 10,
  type: "Essay",
  difficulty: "Application",
  stem: "Câu ngân hàng",
  payload: { modelAnswer: "m", rubric: [{ criterion: "c", points: 1 }] },
  explanation: null,
  status: "Approved",
  revision: 1,
  source: "Manual",
  aiVerification: "NotChecked",
  aiReviewedByAuthor: false,
  createdBy: { id: "other", fullName: "B" },
  createdAt: "2026-11-23T00:00:00Z",
  updatedAt: "2026-11-23T00:00:00Z",
};

function setupApi() {
  mocks.apiGet.mockImplementation((path: string) => {
    if (path === "/questions") return ok({ items: [bankQuestion], page: 1, pageSize: 20, totalItems: 1, totalPages: 1 });
    if (path === "/topics") return ok({ items: [], page: 1, pageSize: 100, totalItems: 0, totalPages: 0 });
    throw new Error(`Unexpected GET ${path}`);
  });
}

function renderComposer(v: ExamVersion, opts: { highlightIds?: string[]; onDirtyChange?: (d: boolean) => void } = {}) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const onDirtyChange = opts.onDirtyChange ?? vi.fn();
  render(
    <QueryClientProvider client={client}>
      <ExamComposer exam={exam} version={v} meId={ME} highlightIds={new Set(opts.highlightIds ?? [])} onDirtyChange={onDirtyChange} />
    </QueryClientProvider>,
  );
  return onDirtyChange;
}

const pointsInput = (n: number) => screen.getByLabelText(`${messages.exams.pointsLabel} ${n}`) as HTMLInputElement;
// :disabled also covers controls locked by an ancestor <fieldset disabled>, unlike the .disabled property.
const isLocked = (el: Element) => el.matches(":disabled");
const save = () => fireEvent.click(screen.getByRole("button", { name: messages.exams.save }));

describe("ExamComposer", () => {
  beforeEach(setupApi);
  afterEach(() => {
    cleanup();
    vi.clearAllMocks();
  });

  it("adds, reorders and saves exact points", async () => {
    mocks.apiPut.mockImplementation((_p: string, init: { body: object }) => ok({ ...version([]), ...init.body, items: [] }));
    renderComposer(version([item("q1", "Câu một", 1)]));
    const bankRow = within((await screen.findByText("Câu ngân hàng")).closest("li") as HTMLElement);
    fireEvent.click(bankRow.getByRole("button", { name: messages.exams.add }));
    fireEvent.click(screen.getByRole("button", { name: `${messages.exams.moveUp} 2` }));
    fireEvent.change(pointsInput(1), { target: { value: "1,5" } });
    save();
    await waitFor(() =>
      expect(mocks.apiPut).toHaveBeenCalledWith("/exam-versions/{vid}", {
        params: { path: { vid: "V1" } },
        body: {
          durationMinutes: 45,
          items: [
            { sourceQuestionId: "q2", points: 1.5 },
            { sourceQuestionId: "q1", points: 1 },
          ],
        },
      }),
    );
  });

  it("splits 10 points evenly with the remainder on the last item", async () => {
    renderComposer(version([item("q1", "A", 1), item("q3", "B", 1), item("q4", "C", 1)]));
    fireEvent.click(screen.getByRole("button", { name: messages.exams.splitEvenly }));
    await waitFor(() => expect(pointsInput(3).value).toBe("3.34"));
    expect(pointsInput(1).value).toBe("3.33");
    expect(pointsInput(2).value).toBe("3.33");
    expect(screen.getByTestId("total-points").textContent).toBe("10");
  });

  it("blocks saving non-positive points", async () => {
    renderComposer(version([item("q1", "A", 1)]));
    fireEvent.change(pointsInput(1), { target: { value: "0" } });
    save();
    expect(await screen.findByText(messages.exams.validation.pointsInvalid)).toBeTruthy();
    expect(mocks.apiPut).not.toHaveBeenCalled();
  });

  it("keeps unreadable items when saving and lets the author remove them", async () => {
    mocks.apiPut.mockImplementation((_p: string, init: { body: { items: { sourceQuestionId: string; points: number }[] } }) =>
      ok(version(init.body.items.map((i) => item(i.sourceQuestionId, i.sourceQuestionId === "q9" ? null : "A", i.points)))),
    );
    renderComposer(version([item("q9", null, 2), item("q1", "A", 1)]));
    expect(screen.getByText(messages.exams.contentUnavailable)).toBeTruthy();
    fireEvent.change(screen.getByLabelText(messages.exams.duration), { target: { value: "50" } });
    save();
    await waitFor(() =>
      expect(mocks.apiPut).toHaveBeenCalledWith("/exam-versions/{vid}", {
        params: { path: { vid: "V1" } },
        body: {
          durationMinutes: 50,
          items: [
            { sourceQuestionId: "q9", points: 2 },
            { sourceQuestionId: "q1", points: 1 },
          ],
        },
      }),
    );
    fireEvent.click(screen.getByRole("button", { name: `${messages.exams.remove} 1` }));
    expect(screen.queryByText(messages.exams.contentUnavailable)).toBeNull();
  });

  it("offers a reload after a save conflict", async () => {
    mocks.apiPut.mockReturnValue(fail(409, { code: "Conflict", title: "Conflict" }));
    renderComposer(version([item("q1", "A", 1)]));
    fireEvent.change(pointsInput(1), { target: { value: "2" } });
    save();
    expect(await screen.findByText(messages.exams.conflictReload)).toBeTruthy();
    expect(screen.getByRole("button", { name: messages.exams.reload })).toBeTruthy();
  });

  it("reports dirty state and clears it after saving", async () => {
    mocks.apiPut.mockImplementation((_p: string, init: { body: { items: { sourceQuestionId: string; points: number }[] } }) =>
      ok({ ...version(init.body.items.map((i) => item(i.sourceQuestionId, "A", i.points))), updatedAt: "2026-11-23T01:00:00Z" }),
    );
    const onDirtyChange = renderComposer(version([item("q1", "A", 1)]));
    fireEvent.change(pointsInput(1), { target: { value: "2" } });
    await waitFor(() => expect(onDirtyChange).toHaveBeenLastCalledWith(true));
    save();
    await waitFor(() => expect(onDirtyChange).toHaveBeenLastCalledWith(false));
    expect(pointsInput(1).value).toBe("2");
  });

  it("keeps unsaved edits when the server copy is refetched in the background", async () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const initial = version([item("q1", "A", 1)]);
    const ui = (v: ExamVersion) => (
      <QueryClientProvider client={client}>
        <ExamComposer exam={exam} version={v} meId={ME} highlightIds={new Set()} onDirtyChange={vi.fn()} />
      </QueryClientProvider>
    );
    const { rerender } = render(ui(initial));
    fireEvent.change(pointsInput(1), { target: { value: "4" } });
    await waitFor(() => expect(screen.getByRole("button", { name: messages.exams.save }).hasAttribute("disabled")).toBe(false));
    rerender(ui({ ...initial, warnings: [{ code: "X", message: "m", sourceQuestionId: null }] } as ExamVersion));
    await new Promise((r) => setTimeout(r, 20));
    expect(pointsInput(1).value).toBe("4");
  });

  it("locks editing while a save is in flight so later edits cannot be overwritten", async () => {
    let finishSave!: (value: unknown) => void;
    mocks.apiPut.mockReturnValue(new Promise((resolve) => (finishSave = resolve)));
    renderComposer(version([item("q1", "A", 1)]));
    const addButton = within((await screen.findByText("Câu ngân hàng")).closest("li") as HTMLElement).getByRole("button", {
      name: messages.exams.add,
    }) as HTMLButtonElement;
    fireEvent.change(pointsInput(1), { target: { value: "2" } });
    save();
    await waitFor(() => expect(isLocked(pointsInput(1))).toBe(true));
    expect(isLocked(screen.getByLabelText(messages.exams.duration))).toBe(true);
    expect(isLocked(screen.getByRole("button", { name: messages.exams.splitEvenly }))).toBe(true);
    expect(isLocked(screen.getByRole("button", { name: `${messages.exams.remove} 1` }))).toBe(true);
    expect(isLocked(addButton)).toBe(true);
    finishSave(await ok(version([item("q1", "A", 2)])));
    await waitFor(() => expect(isLocked(pointsInput(1))).toBe(false));
    expect(pointsInput(1).value).toBe("2");
    expect(isLocked(addButton)).toBe(false);
  });

  it("highlights rows the server reported as not approved", () => {
    renderComposer(version([item("q1", "A", 1), item("q3", "B", 1)]), { highlightIds: ["q1"] });
    expect(screen.getByText("A").closest("li")?.getAttribute("data-highlighted")).toBe("true");
    expect(screen.getByText("B").closest("li")?.getAttribute("data-highlighted")).toBeNull();
  });
});
