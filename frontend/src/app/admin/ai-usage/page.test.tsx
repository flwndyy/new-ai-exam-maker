import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import AiUsagePage from "@/app/admin/ai-usage/page";
import { vi as messages } from "@/messages/vi";

const USER = "00000000-0000-0000-0000-000000000005";

const mocks = vi.hoisted(() => ({
  auth: {
    status: "signedIn",
    me: {
      id: "00000000-0000-0000-0000-000000000001",
      email: "admin@example.invalid",
      fullName: "Synthetic Admin",
      role: "admin",
      departmentId: null as string | null,
      isActive: true,
    },
  },
  apiGet: vi.fn(),
}));

vi.mock("next/navigation", () => ({
  useSearchParams: () => new URLSearchParams(""),
  useRouter: () => ({ push: vi.fn(), replace: vi.fn() }),
}));
vi.mock("@/lib/auth/auth-context", () => ({ useAuth: () => mocks.auth }));
vi.mock("@/lib/api/client", () => ({
  api: { GET: mocks.apiGet, POST: vi.fn(), PUT: vi.fn(), PATCH: vi.fn(), DELETE: vi.fn() },
}));

const ok = (data: unknown) => Promise.resolve({ data, error: undefined, response: new Response(null, { status: 200 }) });

function usagePage(page = 1, totalPages = 3) {
  return {
    items: [{ user: { id: USER, fullName: "Nguyễn Văn A" }, date: "2026-11-23", jobs: 4, tokens: 31200, estimatedCostUsd: 0.091 }],
    page,
    pageSize: 20,
    totalItems: 41,
    totalPages,
    totals: { jobs: 12, tokens: 96400, estimatedCostUsd: 0.281 },
  };
}

function setupApi(usage: () => Promise<unknown> = () => ok(usagePage())) {
  mocks.apiGet.mockImplementation((path: string, init?: { params?: { query?: { page?: number } } }) => {
    switch (path) {
      case "/admin/users":
        return ok({
          items: [{ id: USER, cognitoSub: "s", email: "a@example.invalid", fullName: "Nguyễn Văn A", role: "teacher", departmentId: null, isActive: true, createdAt: "2026-11-01T00:00:00Z" }],
          page: init?.params?.query?.page ?? 1,
          pageSize: 100,
          totalItems: 1,
          totalPages: 1,
        });
      case "/admin/ai-usage":
        return usage();
      default:
        throw new Error(`Unexpected GET ${path}`);
    }
  });
}

const usageCalls = () => mocks.apiGet.mock.calls.filter(([path]) => path === "/admin/ai-usage");

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <AiUsagePage />
    </QueryClientProvider>,
  );
}

describe("admin AI usage page", () => {
  beforeEach(() => {
    mocks.apiGet.mockReset();
    mocks.auth.me.role = "admin";
  });
  afterEach(cleanup);

  it("tells non-admins they have no access and requests nothing", async () => {
    mocks.auth.me.role = "teacher";
    setupApi();
    renderPage();
    expect(await screen.findByText(messages.common.forbidden)).toBeTruthy();
    expect(mocks.apiGet).not.toHaveBeenCalled();
  });

  it("renders rows, totals and the estimate notice", async () => {
    setupApi();
    renderPage();
    expect(await screen.findByText("2026-11-23")).toBeTruthy();
    expect(screen.getByText(messages.aiUsage.estimateNotice)).toBeTruthy();
    expect(screen.getByRole("columnheader", { name: messages.aiUsage.date })).toBeTruthy();
    expect(screen.getByText("31.200")).toBeTruthy();
    expect(screen.getByText("$0,0910")).toBeTruthy();
    expect(screen.getByText(messages.aiUsage.totals("12", "96.400", "$0,2810"))).toBeTruthy();
  });

  it("sends the userId/from/to filter only when Lọc is pressed, back on page 1", async () => {
    setupApi();
    renderPage();
    await screen.findByRole("option", { name: "Nguyễn Văn A" });
    fireEvent.click(screen.getByRole("button", { name: messages.common.nextPage }));
    await waitFor(() => expect(usageCalls().at(-1)?.[1].params.query.page).toBe(2));

    fireEvent.change(screen.getByLabelText(messages.aiUsage.user), { target: { value: USER } });
    fireEvent.change(screen.getByLabelText(messages.aiUsage.from), { target: { value: "2026-11-01" } });
    fireEvent.change(screen.getByLabelText(messages.aiUsage.to), { target: { value: "2026-11-23" } });
    // Editing the inputs alone must not query with the new filter.
    expect(usageCalls().some(([, init]) => init.params.query.userId === USER)).toBe(false);
    fireEvent.click(screen.getByRole("button", { name: messages.aiUsage.filter }));

    await waitFor(() =>
      expect(usageCalls().at(-1)?.[1].params.query).toEqual({
        page: 1,
        pageSize: 20,
        userId: USER,
        from: "2026-11-01",
        to: "2026-11-23",
      }),
    );
  });

  it("shows a validation problem from the API", async () => {
    setupApi(() =>
      Promise.resolve({
        data: undefined,
        error: { code: "ValidationFailed", title: "x", errors: { to: ["Ngày kết thúc phải sau hoặc bằng ngày bắt đầu."] } },
        response: new Response(null, { status: 400 }),
      }),
    );
    renderPage();
    expect(await screen.findByText(/Ngày kết thúc phải sau hoặc bằng ngày bắt đầu\./)).toBeTruthy();
  });
});
