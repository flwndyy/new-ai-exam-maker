import { describe, expect, it } from "vitest";

import { activeNavKey, navItemsFor } from "./nav-items";

describe("navItemsFor", () => {
  it("lists entries per role in order", () => {
    expect(navItemsFor("teacher").map((i) => i.key)).toEqual(["home", "questions", "ai", "exams"]);
    expect(navItemsFor("head").map((i) => i.key)).toEqual(["home", "questions", "ai", "exams", "review"]);
    expect(navItemsFor("admin").map((i) => i.key)).toEqual([
      "home",
      "questions",
      "exams",
      "review",
      "aiUsage",
      "admin",
    ]);
  });

  it("uses trailing-slash hrefs", () => {
    expect(navItemsFor("admin").map((i) => i.href)).toEqual([
      "/",
      "/questions/",
      "/exams/",
      "/review/",
      "/admin/ai-usage/",
      "/admin/",
    ]);
  });
});

describe("activeNavKey", () => {
  const admin = navItemsFor("admin");

  it("matches paths with and without a trailing slash", () => {
    expect(activeNavKey(admin, "/admin/ai-usage")).toBe("aiUsage");
    expect(activeNavKey(admin, "/admin/ai-usage/")).toBe("aiUsage");
    expect(activeNavKey(admin, "/admin/")).toBe("admin");
    expect(activeNavKey(admin, "/admin")).toBe("admin");
  });

  it("matches nested paths by the longest prefix", () => {
    expect(activeNavKey(admin, "/questions/detail")).toBe("questions");
    expect(activeNavKey(admin, "/exams/print/")).toBe("exams");
  });

  it("matches home only on the root", () => {
    expect(activeNavKey(admin, "/")).toBe("home");
    expect(activeNavKey(admin, "/unknown/")).toBeNull();
  });

  it("does not match a different segment sharing a prefix", () => {
    expect(activeNavKey(navItemsFor("teacher"), "/aiexams")).toBeNull();
  });
});
