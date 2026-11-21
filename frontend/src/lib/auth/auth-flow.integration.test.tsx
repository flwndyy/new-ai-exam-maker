// Cross-module flow: the real session, login, API client + auth middleware, AuthProvider,
// AuthGate and callback page work together. Only the network (global fetch), Next's
// navigation hooks, window.location navigation methods and build-time env are faked.

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, cleanup, render, screen, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import CallbackPage from "@/app/auth/callback/page";
import { AuthGate } from "@/components/auth-gate";
import { vi as messages } from "@/messages/vi";
import { AuthProvider, useAuth } from "./auth-context";
import { __resetSessionForTests } from "./session";

type FetchFn = (input: RequestInfo | URL, init?: RequestInit) => Promise<Response>;

const m = vi.hoisted(() => {
  const fetch = vi.fn<FetchFn>();
  // openapi-fetch captures globalThis.fetch, and client.ts reads the API base URL, when
  // the modules load, so both must be in place before any import runs.
  vi.stubGlobal("fetch", fetch);
  vi.stubEnv("NEXT_PUBLIC_API_BASE_URL", "http://localhost:3000/api/v1");
  return {
    fetch,
    routerReplace: vi.fn(),
    pathname: { current: "/" },
    search: { current: "" },
  };
});

vi.mock("next/navigation", () => ({
  usePathname: () => m.pathname.current,
  useRouter: () => ({ replace: m.routerReplace }),
  useSearchParams: () => new URLSearchParams(m.search.current),
}));

const COGNITO = "https://example.auth.amazoncognito.com";
const TOKEN_URL = `${COGNITO}/oauth2/token`;
const ME_URL = "http://localhost:3000/api/v1/me";
const REFRESH_KEY = "exammaker.auth.refresh";
const PENDING_KEY = "exammaker.auth.pending";

const me = {
  id: "11111111-1111-1111-1111-111111111111",
  email: "t@example.com",
  fullName: "Giáo Viên",
  role: "teacher" as const,
  departmentId: "22222222-2222-2222-2222-222222222222",
  departmentName: "Tổ Toán",
  isActive: true,
};

const realLocation = window.location;
const nav = { assign: vi.fn(), replace: vi.fn(), reload: vi.fn() };

function json(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

const unauthorized = () =>
  json(401, { type: "about:blank", title: "Unauthorized", status: 401, code: "Unauthorized" });

function urlOf(input: RequestInfo | URL): string {
  return input instanceof Request ? input.url : String(input);
}

function authorizationOf(input: RequestInfo | URL): string | null {
  return input instanceof Request ? input.headers.get("Authorization") : null;
}

function setPage(pathname: string, search = "") {
  m.pathname.current = pathname;
  m.search.current = search;
  window.history.replaceState(null, "", pathname + search);
}

function Probe() {
  const { status, me: current } = useAuth();
  return (
    <p>
      <span data-testid="status">{status}</span>
      <span data-testid="me">{current ? current.fullName : "none"}</span>
    </p>
  );
}

function renderApp(children: ReactNode) {
  render(
    <QueryClientProvider client={new QueryClient()}>
      <AuthProvider>
        <AuthGate>{children}</AuthGate>
      </AuthProvider>
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  __resetSessionForTests();
  sessionStorage.clear();
  m.fetch.mockReset();
  m.routerReplace.mockReset();
  for (const fn of Object.values(nav)) fn.mockReset();
  vi.stubGlobal("fetch", m.fetch);
  vi.stubEnv("NEXT_PUBLIC_COGNITO_DOMAIN", COGNITO);
  vi.stubEnv("NEXT_PUBLIC_COGNITO_CLIENT_ID", "client");
  vi.stubGlobal("location", {
    get origin() {
      return realLocation.origin;
    },
    get href() {
      return realLocation.href;
    },
    get pathname() {
      return realLocation.pathname;
    },
    get search() {
      return realLocation.search;
    },
    get hash() {
      return realLocation.hash;
    },
    ...nav,
  });
});

afterEach(() => {
  cleanup();
  __resetSessionForTests();
  sessionStorage.clear();
  vi.unstubAllGlobals();
  vi.unstubAllEnvs();
});

describe("auth flow (integration)", () => {
  it("shows the denied page, never a login redirect, when a refreshed token is still refused", async () => {
    sessionStorage.setItem(REFRESH_KEY, "r0");
    setPage("/exams/view/", "?id=1");
    const calls: string[] = [];
    let issued = 0;
    m.fetch.mockImplementation(async (input, init) => {
      const url = urlOf(input);
      if (url === TOKEN_URL) {
        const body = new URLSearchParams(String(init?.body));
        calls.push(`token:${body.get("grant_type")}:${body.get("refresh_token")}`);
        issued += 1;
        return json(200, { access_token: `a${issued}`, expires_in: 3600, token_type: "Bearer" });
      }
      if (url === ME_URL) {
        calls.push(`me:${authorizationOf(input)}`);
        return unauthorized();
      }
      throw new Error(`Unexpected fetch: ${url}`);
    });

    renderApp(<p>protected</p>);

    expect(await screen.findByText(messages.auth.deniedTitle)).toBeTruthy();
    await act(async () => {});
    expect(calls).toEqual([
      "token:refresh_token:r0",
      "me:Bearer a1",
      "token:refresh_token:r0",
      "me:Bearer a2",
    ]);
    expect(screen.queryByText("protected")).toBeNull();
    expect(nav.replace).not.toHaveBeenCalled();
    expect(nav.assign).not.toHaveBeenCalled();
    expect(sessionStorage.getItem(PENDING_KEY)).toBeNull();
    expect(sessionStorage.getItem(REFRESH_KEY)).toBeNull();
  });

  it("completes the callback: exchanges the code, loads /me and returns to the saved page", async () => {
    sessionStorage.setItem(
      PENDING_KEY,
      JSON.stringify({ state: "s", verifier: "v", returnTo: "/exams/view/?id=1" }),
    );
    setPage("/auth/callback/", "?code=c&state=s");
    const calls: string[] = [];
    m.fetch.mockImplementation(async (input, init) => {
      const url = urlOf(input);
      if (url === TOKEN_URL) {
        const body = new URLSearchParams(String(init?.body));
        calls.push(`token:${body.get("grant_type")}:${body.get("code")}:${body.get("code_verifier")}`);
        return json(200, {
          access_token: "a1",
          refresh_token: "r1",
          expires_in: 3600,
          token_type: "Bearer",
        });
      }
      if (url === ME_URL) {
        calls.push(`me:${authorizationOf(input)}`);
        return json(200, me);
      }
      throw new Error(`Unexpected fetch: ${url}`);
    });

    renderApp(
      <>
        <CallbackPage />
        <Probe />
      </>,
    );

    await waitFor(() => expect(m.routerReplace).toHaveBeenCalledWith("/exams/view/?id=1"));
    await waitFor(() => expect(screen.getByTestId("status").textContent).toBe("signedIn"));
    expect(screen.getByTestId("me").textContent).toBe("Giáo Viên");
    expect(calls).toEqual(["token:authorization_code:c:v", "me:Bearer a1"]);
    expect(m.routerReplace).toHaveBeenCalledTimes(1);
    expect(window.location.search).toBe("");
    expect(sessionStorage.getItem(PENDING_KEY)).toBeNull();
    expect(sessionStorage.getItem(REFRESH_KEY)).toBe("r1");
    expect(nav.replace).not.toHaveBeenCalled();
    expect(nav.assign).not.toHaveBeenCalled();
  });
});
