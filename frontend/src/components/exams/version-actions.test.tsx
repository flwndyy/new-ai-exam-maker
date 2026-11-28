import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { VersionActions } from "@/components/exams/version-actions";
import type { components } from "@/lib/api/schema";
import type { Role } from "@/lib/exams/rules";
import { vi as messages } from "@/messages/vi";

type ExamDetail = components["schemas"]["ExamDetail"];
type ExamVersion = components["schemas"]["ExamVersion"];

const ME = "00000000-0000-0000-0000-000000000001";

const mocks = vi.hoisted(() => ({ apiPost: vi.fn() }));
vi.mock("@/lib/api/client", () => ({
  api: { GET: vi.fn(), POST: mocks.apiPost, PUT: vi.fn(), PATCH: vi.fn(), DELETE: vi.fn() },
}));

function ok(data: unknown, status = 200) {
  return Promise.resolve({ data, error: undefined, response: new Response(null, { status }) });
}
function fail(status: number, error: object) {
  return Promise.resolve({ data: undefined, error, response: new Response(null, { status }) });
}

function version(status: string, questionCount = 2): ExamVersion {
  return { id: "V2", examId: "E1", status, questionCount, items: [], warnings: [] } as unknown as ExamVersion;
}

function exam(versions: { id: string; status: string }[], createdBy = ME): ExamDetail {
  return { id: "E1", createdBy: { id: createdBy, fullName: "A" }, versions } as unknown as ExamDetail;
}

type Opts = { exam?: ExamDetail; role?: Role; meId?: string; dirty?: boolean };

function renderActions(v: ExamVersion, o: Opts = {}) {
  const onNotApproved = vi.fn();
  const onVersionCreated = vi.fn();
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <VersionActions
        exam={o.exam ?? exam([{ id: "V2", status: v.status }])}
        version={v}
        role={o.role ?? "teacher"}
        meId={o.meId ?? ME}
        dirty={o.dirty ?? false}
        onNotApproved={onNotApproved}
        onVersionCreated={onVersionCreated}
      />
    </QueryClientProvider>,
  );
  return { onNotApproved, onVersionCreated };
}

const button = (name: string) => screen.queryByRole("button", { name }) as HTMLButtonElement | null;

describe("VersionActions", () => {
  let confirm: ReturnType<typeof vi.spyOn>;
  beforeEach(() => {
    confirm = vi.spyOn(window, "confirm").mockReturnValue(true);
  });
  afterEach(() => {
    cleanup();
    vi.clearAllMocks();
    confirm.mockRestore();
  });

  it("disables submit for an empty draft", () => {
    renderActions(version("Draft", 0));
    expect(button(messages.exams.submit)?.disabled).toBe(true);
  });

  it("disables submit with unsaved changes and explains why", () => {
    renderActions(version("Draft"), { dirty: true });
    expect(button(messages.exams.submit)?.disabled).toBe(true);
    const reason = screen.getByText(messages.exams.saveBeforeSubmit);
    expect(reason.classList.contains("sr-only")).toBe(false);
    expect(button(messages.exams.submit)?.getAttribute("aria-describedby")).toBe(reason.id);
    expect(screen.getAllByText(messages.exams.saveBeforeSubmit)).toHaveLength(1);
  });

  it("shows a visible reason when the draft has no questions", () => {
    renderActions(version("Draft", 0));
    const reason = screen.getByText(messages.exams.addBeforeSubmit);
    expect(reason.classList.contains("sr-only")).toBe(false);
    expect(button(messages.exams.submit)?.getAttribute("aria-describedby")).toBe(reason.id);
  });

  it("shows no reason when submit is enabled", () => {
    renderActions(version("Draft"));
    expect(screen.queryByText(messages.exams.saveBeforeSubmit)).toBeNull();
    expect(screen.queryByText(messages.exams.addBeforeSubmit)).toBeNull();
    expect(button(messages.exams.submit)?.getAttribute("aria-describedby")).toBeNull();
  });

  it("reports unapproved questions returned by submit", async () => {
    mocks.apiPost.mockReturnValue(fail(409, { code: "QuestionsNotApproved", title: "x", invalidQuestionIds: ["q1"] }));
    const { onNotApproved } = renderActions(version("Draft"));
    fireEvent.click(button(messages.exams.submit)!);
    await waitFor(() => expect(onNotApproved).toHaveBeenCalledWith(["q1"]));
    expect(confirm).toHaveBeenCalledWith(messages.exams.confirmSubmit);
    expect(await screen.findByText(messages.exams.notApproved)).toBeTruthy();
  });

  it("submitted versions can only be withdrawn", async () => {
    mocks.apiPost.mockReturnValue(ok(version("Draft")));
    renderActions(version("Submitted"));
    expect(button(messages.exams.submit)).toBeNull();
    expect(button(messages.exams.archive)).toBeNull();
    expect(button(messages.exams.createVersion)).toBeNull();
    fireEvent.click(button(messages.exams.withdraw)!);
    await waitFor(() => expect(mocks.apiPost).toHaveBeenCalledWith("/exam-versions/{vid}/withdraw", { params: { path: { vid: "V2" } } }));
  });

  it("approved version offers archive and a new version copied from it", async () => {
    mocks.apiPost.mockReturnValue(ok({ ...version("Draft"), id: "V3" }, 201));
    const { onVersionCreated } = renderActions(version("Approved"));
    expect(button(messages.exams.archive)).toBeTruthy();
    fireEvent.click(button(messages.exams.createVersion)!);
    await waitFor(() =>
      expect(mocks.apiPost).toHaveBeenCalledWith("/exams/{id}/versions", { params: { path: { id: "E1" } }, body: { fromVersionId: "V2" } }),
    );
    await waitFor(() => expect(onVersionCreated).toHaveBeenCalledWith("V3"));
  });

  it("puts the new version into the cached exam before navigating to it", async () => {
    mocks.apiPost.mockReturnValue(ok({ ...version("Draft"), id: "V3", versionNo: 3 }, 201));
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const cachedExam = exam([{ id: "V2", status: "Approved" }]);
    client.setQueryData(["exam", "E1"], cachedExam);
    let versionsAtNavigation: string[] = [];
    const onVersionCreated = vi.fn(() => {
      versionsAtNavigation = (client.getQueryData(["exam", "E1"]) as ExamDetail).versions.map((v) => v.id);
    });
    render(
      <QueryClientProvider client={client}>
        <VersionActions
          exam={cachedExam}
          version={version("Approved")}
          role="teacher"
          meId={ME}
          dirty={false}
          onNotApproved={vi.fn()}
          onVersionCreated={onVersionCreated}
        />
      </QueryClientProvider>,
    );
    fireEvent.click(button(messages.exams.createVersion)!);
    await waitFor(() => expect(onVersionCreated).toHaveBeenCalledWith("V3"));
    expect(versionsAtNavigation).toEqual(["V3", "V2"]);
  });

  it("creating a version updates latest version id and status in the exam cache", async () => {
    mocks.apiPost.mockReturnValue(ok({ ...version("Draft"), id: "V2", versionNo: 2 }, 201));
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const cachedExam = {
      ...exam([{ id: "V1", status: "Published" }]),
      latestVersionNo: 1,
      latestVersionId: "V1",
      latestVersionStatus: "Published",
    } as unknown as ExamDetail;
    client.setQueryData(["exam", "E1"], cachedExam);
    const onVersionCreated = vi.fn();
    render(
      <QueryClientProvider client={client}>
        <VersionActions
          exam={cachedExam}
          version={{ ...version("Published"), id: "V1" }}
          role="teacher"
          meId={ME}
          dirty={false}
          onNotApproved={vi.fn()}
          onVersionCreated={onVersionCreated}
        />
      </QueryClientProvider>,
    );
    fireEvent.click(button(messages.exams.createVersion)!);
    await waitFor(() => expect(onVersionCreated).toHaveBeenCalledWith("V2"));
    const cached = client.getQueryData(["exam", "E1"]) as ExamDetail;
    expect(cached.latestVersionNo).toBe(2);
    expect(cached.latestVersionId).toBe("V2");
    expect(cached.latestVersionStatus).toBe("Draft");
  });

  it("archive asks for confirmation first", async () => {
    confirm.mockReturnValue(false);
    renderActions(version("Approved"));
    fireEvent.click(button(messages.exams.archive)!);
    expect(confirm).toHaveBeenCalledWith(messages.exams.confirmArchive);
    expect(mocks.apiPost).not.toHaveBeenCalled();
  });

  it("asks before creating a new version over unsaved composer edits", () => {
    confirm.mockReturnValue(false);
    renderActions(version("Rejected"), { dirty: true });
    fireEvent.click(button(messages.exams.createVersion)!);
    expect(confirm).toHaveBeenCalledWith(messages.exams.confirmDiscard);
    expect(mocks.apiPost).not.toHaveBeenCalled();
  });

  it("hides new version while the exam already has a draft", () => {
    renderActions(version("Approved"), { exam: exam([{ id: "V3", status: "Draft" }, { id: "V2", status: "Approved" }]) });
    expect(button(messages.exams.createVersion)).toBeNull();
  });

  it("explains a draft conflict when creating a version", async () => {
    mocks.apiPost.mockReturnValue(fail(409, { code: "Conflict", title: "Conflict" }));
    renderActions(version("Published"));
    fireEvent.click(button(messages.exams.createVersion)!);
    expect(await screen.findByText(messages.exams.draftExists)).toBeTruthy();
  });

  it.each<[Role, string]>([
    ["head", "other"],
    ["admin", "other"],
  ])("%s who is not the author gets no buttons", (role, author) => {
    renderActions(version("Draft"), { role, exam: exam([{ id: "V2", status: "Draft" }], author) });
    expect(screen.queryAllByRole("button")).toHaveLength(0);
  });
});
