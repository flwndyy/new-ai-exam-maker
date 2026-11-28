import createClient from "openapi-fetch";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { __resetSessionForTests, setTokens } from "@/lib/auth/session";
import { createAuthMiddleware } from "./auth-middleware";
import type { paths } from "./schema";

const getAccessTokenMock = vi.hoisted(() => vi.fn());
const refreshTokensMock = vi.hoisted(() => vi.fn());
const forceRefreshSpy = vi.hoisted(() => vi.fn());
const clearSessionSpy = vi.hoisted(() => vi.fn());

vi.mock("./auth-token", () => ({
  getAccessToken: getAccessTokenMock,
  setAccessTokenProvider: vi.fn(),
}));
vi.mock("@/lib/api/auth-token", () => ({
  getAccessToken: getAccessTokenMock,
  setAccessTokenProvider: vi.fn(),
}));

// Only the network call to Cognito is faked; session.ts (single-flight refresh) is real.
vi.mock("@/lib/auth/cognito", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/auth/cognito")>();
  return { ...actual, refreshTokens: refreshTokensMock };
});

// Wrap the real session functions so calls can be counted while behaviour stays real
// unless a test overrides the implementation.
vi.mock("@/lib/auth/session", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/auth/session")>();
  return { ...actual, forceRefresh: forceRefreshSpy, clearSession: clearSessionSpy };
});

type FetchMock = ReturnType<typeof vi.fn<(request: Request) => Promise<Response>>>;

function makeClient(fetchMock: FetchMock) {
  const client = createClient<paths>({
    baseUrl: "http://localhost/api/v1",
    fetch: fetchMock as unknown as typeof fetch,
  });
  client.use(createAuthMiddleware(fetchMock as unknown as typeof fetch));
  return client;
}

function json(status: number, body: unknown = {}): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

let realSession: typeof import("@/lib/auth/session");

describe("createAuthMiddleware", () => {
  beforeEach(async () => {
    realSession = await vi.importActual<typeof import("@/lib/auth/session")>("@/lib/auth/session");
    vi.stubEnv("NEXT_PUBLIC_COGNITO_DOMAIN", "https://example.auth.amazoncognito.com");
    vi.stubEnv("NEXT_PUBLIC_COGNITO_CLIENT_ID", "client");
    __resetSessionForTests();
    getAccessTokenMock.mockReset();
    getAccessTokenMock.mockResolvedValue("t1");
    refreshTokensMock.mockReset();
    forceRefreshSpy.mockReset();
    clearSessionSpy.mockReset();
    forceRefreshSpy.mockImplementation(realSession.forceRefresh);
    clearSessionSpy.mockImplementation(realSession.clearSession);
  });

  afterEach(() => {
    vi.unstubAllEnvs();
  });

  it("attaches the bearer token to outgoing requests", async () => {
    const fetchMock: FetchMock = vi.fn(async () => json(200, { ok: true }));
    const client = makeClient(fetchMock);

    await client.GET("/me");

    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(fetchMock.mock.calls[0][0].headers.get("Authorization")).toBe("Bearer t1");
  });

  it("sends the request without Authorization when the token provider rejects", async () => {
    getAccessTokenMock.mockRejectedValue(new Error("network"));
    const fetchMock: FetchMock = vi.fn(async () => json(401));
    forceRefreshSpy.mockResolvedValue(null);
    const client = makeClient(fetchMock);

    const { response } = await client.GET("/me");

    expect(response.status).toBe(401);
    expect(fetchMock.mock.calls[0][0].headers.has("Authorization")).toBe(false);
  });

  it("refreshes once and replays a POST with the new token on 401", async () => {
    forceRefreshSpy.mockResolvedValue("t2");
    const bodies: string[] = [];
    const responses = [json(401), json(200, { ok: true })];
    const fetchMock: FetchMock = vi.fn(async (request: Request) => {
      bodies.push(await request.clone().text());
      return responses.shift() as Response;
    });
    const client = makeClient(fetchMock);

    const { response } = await client.POST("/ai/generate", {
      body: {
        subjectId: "00000000-0000-4000-8000-000000000001",
        topicId: "00000000-0000-4000-8000-000000000002",
        grade: 10,
        type: "MultipleChoice",
        difficulty: "Recognition",
        count: 1,
      },
    });

    expect(response.status).toBe(200);
    expect(forceRefreshSpy).toHaveBeenCalledTimes(1);
    expect(fetchMock).toHaveBeenCalledTimes(2);
    const [first, second] = [fetchMock.mock.calls[0][0], fetchMock.mock.calls[1][0]];
    expect(first.headers.get("Authorization")).toBe("Bearer t1");
    expect(second.headers.get("Authorization")).toBe("Bearer t2");
    expect(second.method).toBe("POST");
    expect(bodies[0]).toContain("MultipleChoice");
    expect(bodies[1]).toBe(bodies[0]);
    expect(clearSessionSpy).not.toHaveBeenCalled();
  });

  it("clears the session as denied when the replay is still 401, without looping", async () => {
    forceRefreshSpy.mockResolvedValue("t2");
    const fetchMock: FetchMock = vi.fn(async () => json(401));
    const client = makeClient(fetchMock);

    const { response } = await client.GET("/me");

    expect(response.status).toBe(401);
    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(forceRefreshSpy).toHaveBeenCalledTimes(1);
    expect(clearSessionSpy).toHaveBeenCalledTimes(1);
    expect(clearSessionSpy).toHaveBeenCalledWith("denied");
  });

  it("returns the original 401 and does not replay when refresh yields null", async () => {
    forceRefreshSpy.mockResolvedValue(null);
    const fetchMock: FetchMock = vi.fn(async () => json(401));
    const client = makeClient(fetchMock);

    const { response } = await client.GET("/me");

    expect(response.status).toBe(401);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(clearSessionSpy).not.toHaveBeenCalled();
  });

  it("returns the original 401 and leaves the session alone when refresh rejects", async () => {
    forceRefreshSpy.mockRejectedValue(new TypeError("network down"));
    const fetchMock: FetchMock = vi.fn(async () => json(401));
    const client = makeClient(fetchMock);

    const { response } = await client.GET("/me");

    expect(response.status).toBe(401);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(clearSessionSpy).not.toHaveBeenCalled();
  });

  it("does not refresh or touch the session on 403", async () => {
    const fetchMock: FetchMock = vi.fn(async () => json(403));
    const client = makeClient(fetchMock);

    const { response } = await client.GET("/me");

    expect(response.status).toBe(403);
    expect(forceRefreshSpy).not.toHaveBeenCalled();
    expect(clearSessionSpy).not.toHaveBeenCalled();
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it("shares one token refresh across parallel 401s and replays each request once", async () => {
    sessionStorage.setItem("exammaker.auth.refresh", "r1");
    setTokens({ accessToken: "t1", expiresIn: 3600, refreshToken: "r1" });
    refreshTokensMock.mockResolvedValue({ accessToken: "t2", expiresIn: 3600 });
    const fetchMock: FetchMock = vi.fn(async (request: Request) =>
      request.headers.get("Authorization") === "Bearer t2" ? json(200) : json(401),
    );
    const client = makeClient(fetchMock);

    const results = await Promise.all([
      client.GET("/me"),
      client.GET("/departments"),
      client.GET("/subjects"),
    ]);

    expect(results.map((r) => r.response.status)).toEqual([200, 200, 200]);
    expect(refreshTokensMock).toHaveBeenCalledTimes(1);
    expect(fetchMock).toHaveBeenCalledTimes(6);
    expect(clearSessionSpy).not.toHaveBeenCalled();
  });
});
