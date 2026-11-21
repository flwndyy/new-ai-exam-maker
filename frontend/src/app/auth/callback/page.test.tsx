import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { StrictMode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { LoginCallbackError } from "@/lib/auth/login";
import { vi as messages } from "@/messages/vi";
import CallbackPage from "./page";

const m = vi.hoisted(() => ({
  completeLogin: vi.fn(),
  startLogin: vi.fn(),
  replace: vi.fn(),
  search: { current: "code=c&state=s" },
}));

vi.mock("@/lib/auth/login", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/auth/login")>();
  return { ...actual, completeLogin: m.completeLogin, startLogin: m.startLogin };
});
vi.mock("next/navigation", () => ({
  useRouter: () => ({ replace: m.replace }),
  usePathname: () => "/auth/callback/",
  useSearchParams: () => new URLSearchParams(m.search.current),
}));

beforeEach(() => {
  m.completeLogin.mockReset();
  m.startLogin.mockReset();
  m.replace.mockReset();
  m.search.current = "code=c&state=s";
  m.startLogin.mockResolvedValue(undefined);
  window.history.replaceState(null, "", "/auth/callback/?code=c&state=s");
});

afterEach(() => {
  cleanup();
});

describe("CallbackPage", () => {
  it("exchanges the code exactly once under StrictMode and navigates to returnTo", async () => {
    m.completeLogin.mockResolvedValue({ returnTo: "/exams/view/?id=1" });
    render(
      <StrictMode>
        <CallbackPage />
      </StrictMode>,
    );

    await waitFor(() => expect(m.replace).toHaveBeenCalledWith("/exams/view/?id=1"));
    expect(m.completeLogin).toHaveBeenCalledTimes(1);
    expect(m.completeLogin.mock.calls[0][0].get("code")).toBe("c");
    expect(m.replace).toHaveBeenCalledTimes(1);
    expect(screen.queryByText(messages.auth.callbackFailed)).toBeNull();
  });

  it("shows a progress message while completing", async () => {
    m.completeLogin.mockReturnValue(new Promise(() => {}));
    render(<CallbackPage />);
    expect(await screen.findByText(messages.auth.completing)).toBeTruthy();
  });

  it("shows an error and a retry button that restarts login at /", async () => {
    m.completeLogin.mockRejectedValue(new LoginCallbackError("missing_pending"));
    render(<CallbackPage />);

    expect(await screen.findByText(messages.auth.callbackFailed)).toBeTruthy();
    expect(m.replace).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole("button", { name: messages.auth.retry }));
    expect(m.startLogin).toHaveBeenCalledWith("/");
  });

  it("retries login with the original returnTo carried by the error", async () => {
    m.completeLogin.mockRejectedValue(new LoginCallbackError("state_mismatch", "/exams/view/?id=1"));
    render(<CallbackPage />);

    fireEvent.click(await screen.findByRole("button", { name: messages.auth.retry }));
    expect(m.startLogin).toHaveBeenCalledWith("/exams/view/?id=1");
  });

  it("removes code and state from the address bar before the exchange settles", async () => {
    m.completeLogin.mockReturnValue(new Promise(() => {}));
    render(<CallbackPage />);

    await screen.findByText(messages.auth.completing);
    expect(window.location.pathname).toBe("/auth/callback/");
    expect(window.location.search).toBe("");
    // The exchange still received the original parameters.
    expect(m.completeLogin.mock.calls[0][0].get("code")).toBe("c");
    expect(m.completeLogin.mock.calls[0][0].get("state")).toBe("s");
  });

  it("removes code and state from the address bar when the exchange fails", async () => {
    m.completeLogin.mockRejectedValue(new LoginCallbackError("token_exchange_failed", "/"));
    render(<CallbackPage />);

    await screen.findByText(messages.auth.callbackFailed);
    expect(window.location.search).toBe("");
  });
});
