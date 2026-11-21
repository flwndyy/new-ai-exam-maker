import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { StrictMode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { AuthProvider, useAuth } from "@/lib/auth/auth-context";
import { vi as messages } from "@/messages/vi";
import { AuthGate } from "./auth-gate";

const m = vi.hoisted(() => {
  const listeners = new Set<(s: string) => void>();
  const session = { status: "unknown" };
  // De-duplicates exactly like the real session.ts setStatus: no event when unchanged.
  const emit = (s: string) => {
    if (session.status === s) return;
    session.status = s;
    for (const l of [...listeners]) l(s);
  };
  return {
    listeners,
    session,
    emit,
    pathname: { current: "/" },
    getAuthConfig: vi.fn(),
    revokeToken: vi.fn(),
    startLogin: vi.fn(),
    restoreSession: vi.fn(),
    clearSession: vi.fn(),
    getRefreshToken: vi.fn(),
    getStatus: vi.fn(),
    installAccessTokenProvider: vi.fn(),
    apiGet: vi.fn(),
  };
});

vi.mock("next/navigation", () => ({
  usePathname: () => m.pathname.current,
  useRouter: () => ({ replace: vi.fn() }),
  useSearchParams: () => new URLSearchParams(),
}));
vi.mock("@/lib/auth/config", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/auth/config")>();
  return { ...actual, getAuthConfig: m.getAuthConfig };
});
vi.mock("@/lib/auth/cognito", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/auth/cognito")>();
  return { ...actual, revokeToken: m.revokeToken };
});
vi.mock("@/lib/auth/login", () => ({ startLogin: m.startLogin }));
vi.mock("@/lib/auth/session", () => ({
  restoreSession: m.restoreSession,
  clearSession: m.clearSession,
  getRefreshToken: m.getRefreshToken,
  getStatus: m.getStatus,
  installAccessTokenProvider: m.installAccessTokenProvider,
  subscribeSession: (l: (s: string) => void) => {
    m.listeners.add(l);
    return () => {
      m.listeners.delete(l);
    };
  },
}));
vi.mock("@/lib/api/client", () => ({ api: { GET: m.apiGet } }));

const cfg = { domain: "https://example.auth.amazoncognito.com", clientId: "client" };
const me = {
  id: "11111111-1111-1111-1111-111111111111",
  email: "t@example.com",
  fullName: "Giáo Viên",
  role: "teacher" as const,
  departmentId: null,
  departmentName: null,
  isActive: true,
};
const assign = vi.fn();

function LogoutButton() {
  const { logout } = useAuth();
  return <button onClick={() => void logout()}>do-logout</button>;
}

function renderGate() {
  render(
    <QueryClientProvider client={new QueryClient()}>
      <AuthProvider>
        <AuthGate>
          <p>protected</p>
          <LogoutButton />
        </AuthGate>
      </AuthProvider>
    </QueryClientProvider>,
  );
}

function setUrl(path: string, search = "", hash = "") {
  m.pathname.current = path;
  vi.stubGlobal("location", {
    ...window.location,
    origin: "http://localhost:3000",
    pathname: path,
    search,
    hash,
    assign,
  });
}

function sessionResolves(status: "signedIn" | "signedOut" | "denied") {
  m.restoreSession.mockImplementation(async () => {
    m.emit(status);
    return status;
  });
}

beforeEach(() => {
  m.listeners.clear();
  for (const fn of Object.values(m)) {
    if (typeof fn === "function" && "mockReset" in fn) (fn as ReturnType<typeof vi.fn>).mockReset();
  }
  assign.mockReset();
  m.getAuthConfig.mockReturnValue(cfg);
  m.session.status = "unknown";
  m.getStatus.mockImplementation(() => m.session.status);
  m.getRefreshToken.mockReturnValue("refresh-token");
  m.revokeToken.mockResolvedValue(undefined);
  m.startLogin.mockResolvedValue(undefined);
  m.clearSession.mockImplementation((next: string) => m.emit(next));
  m.apiGet.mockResolvedValue({ data: me, response: { status: 200 } });
  setUrl("/");
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe("AuthGate", () => {
  it("starts login with the current path and query when signed out", async () => {
    setUrl("/exams/view/", "?id=1");
    sessionResolves("signedOut");
    renderGate();

    await waitFor(() => expect(m.startLogin).toHaveBeenCalledWith("/exams/view/?id=1"));
    expect(m.startLogin).toHaveBeenCalledTimes(1);
    expect(screen.queryByText("protected")).toBeNull();
    expect(screen.getByText(messages.auth.redirecting)).toBeTruthy();
  });

  it("keeps the location hash in returnTo", async () => {
    setUrl("/exams/view/", "?id=1", "#q2");
    sessionResolves("signedOut");
    renderGate();

    await waitFor(() => expect(m.startLogin).toHaveBeenCalledWith("/exams/view/?id=1#q2"));
  });

  it("starts login exactly once under StrictMode when there is no stored session", async () => {
    sessionResolves("signedOut");
    render(
      <StrictMode>
        <QueryClientProvider client={new QueryClient()}>
          <AuthProvider>
            <AuthGate>
              <p>protected</p>
            </AuthGate>
          </AuthProvider>
        </QueryClientProvider>
      </StrictMode>,
    );

    await waitFor(() => expect(m.startLogin).toHaveBeenCalledTimes(1));
    expect(screen.getByText(messages.auth.redirecting)).toBeTruthy();
    expect(screen.queryByText(messages.common.loading)).toBeNull();
  });

  it("renders children on the callback path without starting a login", async () => {
    setUrl("/auth/callback/", "?code=c&state=s");
    sessionResolves("signedOut");
    renderGate();

    expect(screen.getByText("protected")).toBeTruthy();
    await act(async () => {});
    expect(m.startLogin).not.toHaveBeenCalled();
  });

  it("shows a loading message while the session is being restored", () => {
    m.restoreSession.mockReturnValue(new Promise(() => {}));
    renderGate();
    expect(screen.getByText(messages.common.loading)).toBeTruthy();
    expect(screen.queryByText("protected")).toBeNull();
  });

  it("renders children once signed in", async () => {
    sessionResolves("signedIn");
    renderGate();
    expect(await screen.findByText("protected")).toBeTruthy();
    expect(m.startLogin).not.toHaveBeenCalled();
  });

  it("shows the denied page with a logout button and never starts a login", async () => {
    sessionResolves("denied");
    renderGate();

    expect(await screen.findByText(messages.auth.deniedTitle)).toBeTruthy();
    expect(screen.getByText(messages.auth.deniedBody)).toBeTruthy();
    expect(screen.queryByText("protected")).toBeNull();
    expect(m.startLogin).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole("button", { name: messages.auth.logout }));
    await waitFor(() => expect(assign).toHaveBeenCalledTimes(1));
    expect(m.startLogin).not.toHaveBeenCalled();
  });

  it("shows the misconfigured message and does not log in", async () => {
    m.getAuthConfig.mockReturnValue(null);
    renderGate();
    expect(screen.getByText(messages.auth.misconfigured)).toBeTruthy();
    await act(async () => {});
    expect(m.restoreSession).not.toHaveBeenCalled();
    expect(m.startLogin).not.toHaveBeenCalled();
  });

  it("shows a network error with a retry button", async () => {
    m.restoreSession.mockRejectedValueOnce(new TypeError("network"));
    renderGate();
    expect(await screen.findByText(messages.auth.networkError)).toBeTruthy();

    sessionResolves("signedIn");
    fireEvent.click(screen.getByRole("button", { name: messages.auth.retry }));
    expect(await screen.findByText("protected")).toBeTruthy();
  });

  it("logout navigates only to the Cognito logout URL, never back to login", async () => {
    sessionResolves("signedIn");
    renderGate();
    await screen.findByText("protected");

    fireEvent.click(screen.getByText("do-logout"));

    await waitFor(() => expect(assign).toHaveBeenCalledTimes(1));
    expect(new URL(assign.mock.calls[0][0] as string).pathname).toBe("/logout");
    expect(m.startLogin).not.toHaveBeenCalled();
    expect(screen.queryByText(messages.auth.redirecting)).toBeNull();
  });
});
