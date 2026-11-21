import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";

import { StrictMode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { UserMenu } from "@/components/user-menu";
import { vi as messages } from "@/messages/vi";
import { AuthProvider, isPublicPath, useAuth } from "./auth-context";

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

vi.mock("./config", async (importOriginal) => {
  const actual = await importOriginal<typeof import("./config")>();
  return { ...actual, getAuthConfig: m.getAuthConfig };
});
vi.mock("./cognito", async (importOriginal) => {
  const actual = await importOriginal<typeof import("./cognito")>();
  return { ...actual, revokeToken: m.revokeToken };
});
vi.mock("./login", () => ({ startLogin: m.startLogin }));
vi.mock("./session", () => ({
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
const headMe = {
  id: "11111111-1111-1111-1111-111111111111",
  email: "head@example.com",
  fullName: "Trần Thị Tổ Trưởng",
  role: "head" as const,
  departmentId: "22222222-2222-2222-2222-222222222222",
  departmentName: "Tổ Toán",
  isActive: true,
};

function Probe() {
  const { status, me, hasRole, logout, retry } = useAuth();
  return (
    <div>
      <span data-testid="status">{status}</span>
      <span data-testid="me">{me ? me.fullName : "none"}</span>
      <span data-testid="is-head">{String(hasRole("head"))}</span>
      <span data-testid="is-admin-or-head">{String(hasRole("admin", "head"))}</span>
      <button onClick={() => void logout()}>do-logout</button>
      <button onClick={retry}>do-retry</button>
    </div>
  );
}

function renderApp(ui = <Probe />, queryClient = new QueryClient()) {
  render(
    <QueryClientProvider client={queryClient}>
      <AuthProvider>{ui}</AuthProvider>
    </QueryClientProvider>,
  );
  return queryClient;
}

const assign = vi.fn();
const replace = vi.fn();
const reload = vi.fn();

beforeEach(() => {
  m.listeners.clear();
  for (const fn of [
    m.getAuthConfig,
    m.revokeToken,
    m.startLogin,
    m.restoreSession,
    m.clearSession,
    m.getRefreshToken,
    m.getStatus,
    m.installAccessTokenProvider,
    m.apiGet,
    assign,
    replace,
    reload,
  ]) {
    fn.mockReset();
  }
  m.getAuthConfig.mockReturnValue(cfg);
  m.session.status = "unknown";
  m.getStatus.mockImplementation(() => m.session.status);
  m.getRefreshToken.mockReturnValue("refresh-token");
  m.revokeToken.mockResolvedValue(undefined);
  m.startLogin.mockResolvedValue(undefined);
  m.clearSession.mockImplementation((next: string) => m.emit(next));
  // Restoring a session emits signedIn (as setTokens does) and resolves "signedIn".
  m.restoreSession.mockImplementation(async () => {
    m.emit("signedIn");
    return "signedIn";
  });
  m.apiGet.mockResolvedValue({ data: headMe, response: { status: 200 } });
  vi.stubGlobal("location", {
    ...window.location,
    origin: "http://localhost:3000",
    assign,
    replace,
    reload,
  });
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe("isPublicPath", () => {
  it("accepts the callback path with and without a trailing slash only", () => {
    expect(isPublicPath("/auth/callback/")).toBe(true);
    expect(isPublicPath("/auth/callback")).toBe(true);
    expect(isPublicPath("/")).toBe(false);
    expect(isPublicPath("/exams/view/")).toBe(false);
    expect(isPublicPath("/auth/callback/extra/")).toBe(false);
  });
});

describe("AuthProvider", () => {
  it("is misconfigured without Cognito config and makes no network or login calls", async () => {
    m.getAuthConfig.mockReturnValue(null);
    renderApp();
    expect(screen.getByTestId("status").textContent).toBe("misconfigured");
    await act(async () => {});
    expect(m.restoreSession).not.toHaveBeenCalled();
    expect(m.apiGet).not.toHaveBeenCalled();
    expect(m.startLogin).not.toHaveBeenCalled();
  });

  it("restores the session, fetches /me once and exposes the role", async () => {
    renderApp();
    await waitFor(() => expect(screen.getByTestId("status").textContent).toBe("signedIn"));
    expect(m.installAccessTokenProvider).toHaveBeenCalled();
    expect(m.apiGet).toHaveBeenCalledTimes(1);
    expect(m.apiGet).toHaveBeenCalledWith("/me");
    expect(screen.getByTestId("me").textContent).toBe("Trần Thị Tổ Trưởng");
    expect(screen.getByTestId("is-head").textContent).toBe("true");
    expect(screen.getByTestId("is-admin-or-head").textContent).toBe("true");
  });

  it("is signedOut when there is no stored session", async () => {
    m.restoreSession.mockImplementation(async () => {
      m.emit("signedOut");
      return "signedOut";
    });
    renderApp();
    await waitFor(() => expect(screen.getByTestId("status").textContent).toBe("signedOut"));
    expect(m.apiGet).not.toHaveBeenCalled();
    expect(screen.getByTestId("is-head").textContent).toBe("false");
  });

  it("reflects a 401 from /me through the session event as denied", async () => {
    m.apiGet.mockImplementation(async () => {
      m.emit("denied");
      return { error: {}, response: { status: 401 } };
    });
    renderApp();
    await waitFor(() => expect(screen.getByTestId("status").textContent).toBe("denied"));
    expect(screen.getByTestId("me").textContent).toBe("none");
  });

  it("settles on signedOut under StrictMode when there is no stored session", async () => {
    m.restoreSession.mockImplementation(async () => {
      m.emit("signedOut");
      return "signedOut";
    });
    render(
      <StrictMode>
        <QueryClientProvider client={new QueryClient()}>
          <AuthProvider>
            <Probe />
          </AuthProvider>
        </QueryClientProvider>
      </StrictMode>,
    );
    await waitFor(() => expect(screen.getByTestId("status").textContent).toBe("signedOut"));
    expect(m.apiGet).not.toHaveBeenCalled();
  });

  it("goes to error when /me returns 401 but no session event arrived", async () => {
    // Middleware returns the original 401 untouched on a transient refresh failure.
    m.apiGet.mockResolvedValue({ error: {}, response: { status: 401 } });
    renderApp();
    await waitFor(() => expect(screen.getByTestId("status").textContent).toBe("error"));
    expect(screen.getByTestId("me").textContent).toBe("none");
  });

  it("goes to error on a /me network failure and retry fetches /me again", async () => {
    m.apiGet.mockRejectedValueOnce(new TypeError("network"));
    renderApp();
    await waitFor(() => expect(screen.getByTestId("status").textContent).toBe("error"));

    m.apiGet.mockResolvedValue({ data: headMe, response: { status: 200 } });
    fireEvent.click(screen.getByText("do-retry"));
    await waitFor(() => expect(screen.getByTestId("status").textContent).toBe("signedIn"));
  });

  it("goes to error when restoreSession rejects and retry restores again", async () => {
    m.restoreSession.mockRejectedValueOnce(new TypeError("network"));
    renderApp();
    await waitFor(() => expect(screen.getByTestId("status").textContent).toBe("error"));
    expect(m.apiGet).not.toHaveBeenCalled();

    fireEvent.click(screen.getByText("do-retry"));
    await waitFor(() => expect(screen.getByTestId("status").textContent).toBe("signedIn"));
    expect(m.restoreSession).toHaveBeenCalledTimes(2);
  });

  it("login() delegates to startLogin with the given returnTo", async () => {
    function LoginButton() {
      const { login } = useAuth();
      return <button onClick={() => void login("/x/")}>go</button>;
    }
    renderApp(<LoginButton />);
    fireEvent.click(screen.getByText("go"));
    expect(m.startLogin).toHaveBeenCalledWith("/x/");
  });

  describe("back/forward cache", () => {
    function pageshow(persisted: boolean): Event {
      return typeof PageTransitionEvent === "function"
        ? new PageTransitionEvent("pageshow", { persisted })
        : Object.assign(new Event("pageshow"), { persisted });
    }

    it("reloads the page when it is restored from the bfcache", async () => {
      renderApp();
      await waitFor(() => expect(screen.getByTestId("status").textContent).toBe("signedIn"));

      fireEvent.click(screen.getByText("do-logout"));
      await waitFor(() => expect(assign).toHaveBeenCalledTimes(1));
      // Back from the Cognito logout page restores the frozen "loggingOut" state.
      act(() => {
        window.dispatchEvent(pageshow(true));
      });
      expect(reload).toHaveBeenCalledTimes(1);
    });

    it("ignores a normal (non-persisted) pageshow", async () => {
      renderApp();
      await waitFor(() => expect(screen.getByTestId("status").textContent).toBe("signedIn"));
      act(() => {
        window.dispatchEvent(pageshow(false));
      });
      expect(reload).not.toHaveBeenCalled();
    });

    it("stops listening after unmount", async () => {
      renderApp();
      await waitFor(() => expect(screen.getByTestId("status").textContent).toBe("signedIn"));
      cleanup();
      window.dispatchEvent(pageshow(true));
      expect(reload).not.toHaveBeenCalled();
    });
  });

  describe("logout", () => {
    it("navigates once to the Cognito logout URL, clears the cache and never starts a login", async () => {
      const queryClient = new QueryClient();
      queryClient.setQueryData(["secret"], { a: 1 });
      renderApp(<Probe />, queryClient);
      await waitFor(() => expect(screen.getByTestId("status").textContent).toBe("signedIn"));

      fireEvent.click(screen.getByText("do-logout"));

      await waitFor(() => expect(assign).toHaveBeenCalledTimes(1));
      const url = new URL(assign.mock.calls[0][0] as string);
      expect(url.origin + url.pathname).toBe(`${cfg.domain}/logout`);
      expect(url.searchParams.get("logout_uri")).toBe("http://localhost:3000/");
      expect(m.revokeToken).toHaveBeenCalledWith(cfg, "refresh-token", expect.any(AbortSignal));
      expect(m.clearSession).toHaveBeenCalledWith("signedOut");
      expect(m.startLogin).not.toHaveBeenCalled();
      expect(queryClient.getQueryCache().getAll()).toHaveLength(0);
      // The provider ignored the signedOut event emitted by clearSession.
      expect(screen.getByTestId("status").textContent).toBe("loggingOut");
    });

    it("clears the local session and the query cache before awaiting revocation", async () => {
      const queryClient = new QueryClient();
      queryClient.setQueryData(["secret"], { a: 1 });
      let atRevoke: { cleared: unknown[][]; cached: number } | null = null;
      m.revokeToken.mockImplementation(async () => {
        atRevoke = {
          cleared: [...m.clearSession.mock.calls],
          cached: queryClient.getQueryCache().getAll().length,
        };
      });
      renderApp(<Probe />, queryClient);
      await waitFor(() => expect(screen.getByTestId("status").textContent).toBe("signedIn"));

      fireEvent.click(screen.getByText("do-logout"));

      await waitFor(() => expect(assign).toHaveBeenCalledTimes(1));
      expect(atRevoke).toEqual({ cleared: [["signedOut"]], cached: 0 });
      expect(m.startLogin).not.toHaveBeenCalled();
      expect(screen.getByTestId("status").textContent).toBe("loggingOut");
    });

    it("bounds revocation with a 5 s timeout and still navigates when it never settles", async () => {
      // An already-aborted signal stands in for the 5 s timeout having elapsed.
      const timeout = vi.spyOn(AbortSignal, "timeout").mockReturnValue(AbortSignal.abort());
      try {
        // Never resolves on its own; only the abort signal can end it (as fetch does).
        m.revokeToken.mockImplementation(
          (_cfg: unknown, _token: string, signal?: AbortSignal) =>
            new Promise<void>((_resolve, reject) => {
              if (signal?.aborted) reject(signal.reason);
              signal?.addEventListener("abort", () => reject(signal.reason));
            }),
        );
        renderApp();
        await waitFor(() => expect(screen.getByTestId("status").textContent).toBe("signedIn"));

        fireEvent.click(screen.getByText("do-logout"));

        await waitFor(() => expect(assign).toHaveBeenCalledTimes(1));
        expect(timeout).toHaveBeenCalledWith(5000);
        expect(m.clearSession).toHaveBeenCalledWith("signedOut");
      } finally {
        timeout.mockRestore();
      }
    });

    it("still signs out when token revocation fails", async () => {
      m.revokeToken.mockRejectedValue(new Error("network"));
      renderApp();
      await waitFor(() => expect(screen.getByTestId("status").textContent).toBe("signedIn"));

      fireEvent.click(screen.getByText("do-logout"));

      await waitFor(() => expect(assign).toHaveBeenCalledTimes(1));
      expect(m.clearSession).toHaveBeenCalledWith("signedOut");
    });
  });
});

describe("UserMenu", () => {
  it("shows name, role label and department, and a logout button", async () => {
    renderApp(<UserMenu />);
    expect(await screen.findByText("Trần Thị Tổ Trưởng")).toBeTruthy();
    expect(screen.getByText(`${messages.auth.roles.head} · Tổ Toán`)).toBeTruthy();
    expect(screen.getByRole("button", { name: messages.auth.logout })).toBeTruthy();
  });

  it("renders nothing while signed out", async () => {
    m.restoreSession.mockImplementation(async () => {
      m.emit("signedOut");
      return "signedOut";
    });
    const { container } = render(
      <QueryClientProvider client={new QueryClient()}>
        <AuthProvider>
          <UserMenu />
        </AuthProvider>
      </QueryClientProvider>,
    );
    await act(async () => {});
    expect(container.innerHTML).toBe("");
  });
});
