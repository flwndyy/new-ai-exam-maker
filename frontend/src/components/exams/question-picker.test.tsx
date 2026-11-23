import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { QuestionPicker } from "@/components/exams/question-picker";
import { vi as messages } from "@/messages/vi";

const ME = "00000000-0000-0000-0000-000000000001";
const SUBJECT = "00000000-0000-0000-0000-000000000020";
const TOPIC = "00000000-0000-0000-0000-000000000030";

const mocks = vi.hoisted(() => ({ apiGet: vi.fn() }));
vi.mock("@/lib/api/client", () => ({
  api: { GET: mocks.apiGet, POST: vi.fn(), PUT: vi.fn(), PATCH: vi.fn(), DELETE: vi.fn() },
}));

function question(id: string, stem: string, status: string, createdBy: string) {
  return {
    id,
    subjectId: SUBJECT,
    topicId: TOPIC,
    departmentId: "d",
    grade: 10,
    type: "Essay",
    difficulty: "Recognition",
    stem,
    payload: { modelAnswer: "m", rubric: [{ criterion: "c", points: 1 }] },
    explanation: null,
    status,
    revision: 1,
    source: "Manual",
    aiVerification: "NotChecked",
    aiReviewedByAuthor: false,
    createdBy: { id: createdBy, fullName: "X" },
    createdAt: "2026-11-23T00:00:00Z",
    updatedAt: "2026-11-23T00:00:00Z",
  };
}

const ownDraft = question("q1", "Câu nháp của tôi", "Draft", ME);
const otherApproved = question("q2", "Câu đã duyệt", "Approved", "other");
const otherSubmitted = question("q3", "Câu chờ duyệt", "Submitted", "other");
const alreadyPicked = question("q4", "Câu đã chọn", "Approved", "other");

function ok(data: unknown) {
  return Promise.resolve({ data, error: undefined, response: new Response(null, { status: 200 }) });
}

function setupApi() {
  mocks.apiGet.mockImplementation((path: string) => {
    if (path === "/questions") {
      const items = [ownDraft, otherApproved, otherSubmitted, alreadyPicked];
      return ok({ items, page: 1, pageSize: 20, totalItems: items.length, totalPages: 1 });
    }
    if (path === "/topics") {
      return ok({ items: [{ id: TOPIC, subjectId: SUBJECT, grade: 10, name: "Nguyên tử", sortOrder: 1 }], page: 1, pageSize: 100, totalItems: 1, totalPages: 1 });
    }
    throw new Error(`Unexpected GET ${path}`);
  });
}

function renderPicker(onAdd = vi.fn()) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <QuestionPicker subjectId={SUBJECT} grade={10} meId={ME} selectedIds={new Set(["q4"])} onAdd={onAdd} />
    </QueryClientProvider>,
  );
  return onAdd;
}

function lastQuestionsQuery() {
  const calls = mocks.apiGet.mock.calls.filter(([p]) => p === "/questions");
  return calls[calls.length - 1][1].params.query;
}

async function row(stem: string) {
  return within((await screen.findByText(stem)).closest("li") as HTMLElement);
}

describe("QuestionPicker", () => {
  beforeEach(setupApi);
  afterEach(() => {
    cleanup();
    vi.clearAllMocks();
  });

  it("always filters by the exam subject and grade", async () => {
    renderPicker();
    await screen.findByText(ownDraft.stem);
    expect(lastQuestionsQuery()).toMatchObject({ subjectId: SUBJECT, grade: 10, pageSize: 20 });
  });

  it("own draft can be added but warns it needs approval", async () => {
    renderPicker();
    const r = await row(ownDraft.stem);
    expect(r.getByRole("button", { name: messages.exams.add })).toBeTruthy();
    expect(r.getByText(messages.exams.needsApproval)).toBeTruthy();
  });

  it("other teacher's approved question can be added without warning", async () => {
    renderPicker();
    const r = await row(otherApproved.stem);
    expect(r.getByRole("button", { name: messages.exams.add })).toBeTruthy();
    expect(r.queryByText(messages.exams.needsApproval)).toBeNull();
  });

  it("other teacher's submitted question and already-picked questions have no add button", async () => {
    renderPicker();
    expect((await row(otherSubmitted.stem)).queryByRole("button", { name: messages.exams.add })).toBeNull();
    expect((await row(alreadyPicked.stem)).queryByRole("button", { name: messages.exams.add })).toBeNull();
  });

  it("clicking add passes the question", async () => {
    const onAdd = renderPicker();
    fireEvent.click((await row(otherApproved.stem)).getByRole("button", { name: messages.exams.add }));
    expect(onAdd).toHaveBeenCalledWith(expect.objectContaining({ id: "q2" }));
  });

  it("topic filter re-queries with topicId", async () => {
    renderPicker();
    await screen.findByRole("option", { name: "Nguyên tử" });
    fireEvent.change(screen.getByLabelText(messages.questions.topic), { target: { value: TOPIC } });
    await waitFor(() => expect(lastQuestionsQuery().topicId).toBe(TOPIC));
  });
});
