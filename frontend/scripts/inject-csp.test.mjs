// @vitest-environment node
import { mkdtempSync, mkdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { buildCsp, hashInlineScripts, injectCsp, main } from "./inject-csp.mjs";

describe("hashInlineScripts", () => {
  it("hashes inline scripts and skips scripts with src", () => {
    expect(hashInlineScripts('<script>alert(1)</script><script src="/a.js"></script>')).toEqual([
      "'sha256-bhHHL3z2vDgxUt0W3dWQOrprscmda2Y5pLsLg4GF+pI='",
    ]);
  });

  it("hashes scripts that carry attributes other than src", () => {
    expect(hashInlineScripts('<script id="x" async="">alert(1)</script>')).toEqual([
      "'sha256-bhHHL3z2vDgxUt0W3dWQOrprscmda2Y5pLsLg4GF+pI='",
    ]);
  });

  it("returns an empty list when there are no inline scripts", () => {
    expect(hashInlineScripts("<html></html>")).toEqual([]);
  });
});

describe("buildCsp", () => {
  const hash = "'sha256-abc='";

  it("never allows eval or inline scripts", () => {
    const csp = buildCsp({ scriptHashes: [hash] });
    expect(csp).not.toContain("unsafe-eval");
    const scriptSrc = csp.split("; ").find((d) => d.startsWith("script-src"));
    expect(scriptSrc).toBe(`script-src 'self' ${hash}`);
    expect(scriptSrc).not.toContain("unsafe-inline");
    expect(csp).toContain("object-src 'none'");
  });

  it("omits empty sources when no Cognito domain or API origin is given", () => {
    const csp = buildCsp({ scriptHashes: [] });
    expect(csp).toBe(
      "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; connect-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'",
    );
  });

  it("adds Cognito domain and API origin where required", () => {
    const csp = buildCsp({
      scriptHashes: [hash],
      cognitoDomain: "https://auth.example.com",
      apiOrigin: "https://api.example.com",
    });
    expect(csp).toContain("connect-src 'self' https://auth.example.com https://api.example.com;");
    expect(csp).toContain("form-action 'self' https://auth.example.com");
  });
});

describe("injectCsp", () => {
  const csp = "default-src 'self'";

  it("places the meta right after the charset meta, before the first script", () => {
    const html = '<html><head><meta charSet="utf-8"/><script>a()</script></head></html>';
    const out = injectCsp(html, csp);
    expect(out.indexOf("Content-Security-Policy")).toBeLessThan(out.indexOf("<script"));
    expect(out).toContain(
      `<meta charSet="utf-8"/><meta http-equiv="Content-Security-Policy" content="${csp}"/>`,
    );
  });

  it("falls back to right after the opening head tag", () => {
    const out = injectCsp('<html><head lang="vi"><script>a()</script></head></html>', csp);
    expect(out).toContain(`<head lang="vi"><meta http-equiv="Content-Security-Policy"`);
  });

  it("escapes double quotes in the policy value", () => {
    const out = injectCsp("<head></head>", 'a "b"');
    expect(out).toContain('content="a &quot;b&quot;"');
  });

  it("throws when there is no head", () => {
    expect(() => injectCsp("<html></html>", csp)).toThrow(/<head>/);
  });

  it("throws when a CSP meta already exists (second call)", () => {
    const once = injectCsp("<head></head>", csp);
    expect(() => injectCsp(once, csp)).toThrow(/already/i);
  });
});

describe("main", () => {
  it("injects into every html file under the output directory", () => {
    const dir = mkdtempSync(join(tmpdir(), "inject-csp-"));
    try {
      mkdirSync(join(dir, "auth", "callback"), { recursive: true });
      const page = "<html><head><script>alert(1)</script></head><body></body></html>";
      writeFileSync(join(dir, "index.html"), page);
      writeFileSync(join(dir, "auth", "callback", "index.html"), page);
      main(dir);
      for (const file of [join(dir, "index.html"), join(dir, "auth", "callback", "index.html")]) {
        const html = readFileSync(file, "utf8");
        expect(html.match(/Content-Security-Policy/g)).toHaveLength(1);
        expect(html).toContain("sha256-bhHHL3z2vDgxUt0W3dWQOrprscmda2Y5pLsLg4GF+pI=");
      }
    } finally {
      rmSync(dir, { recursive: true, force: true });
    }
  });

  it("fails loudly when a page has no head", () => {
    const dir = mkdtempSync(join(tmpdir(), "inject-csp-"));
    try {
      writeFileSync(join(dir, "index.html"), "<html></html>");
      expect(() => main(dir)).toThrow(/index\.html/);
    } finally {
      rmSync(dir, { recursive: true, force: true });
    }
  });
});

describe("main environment handling", () => {
  const KEYS = ["NEXT_PUBLIC_COGNITO_DOMAIN", "NEXT_PUBLIC_API_BASE_URL"];
  const saved = {};
  let dir;

  beforeEach(() => {
    for (const key of KEYS) {
      saved[key] = process.env[key];
      delete process.env[key];
    }
    dir = mkdtempSync(join(tmpdir(), "inject-csp-env-"));
    writeFileSync(join(dir, "index.html"), "<html><head></head><body></body></html>");
  });

  afterEach(() => {
    for (const key of KEYS) {
      if (saved[key] === undefined) delete process.env[key];
      else process.env[key] = saved[key];
    }
    vi.restoreAllMocks();
    rmSync(dir, { recursive: true, force: true });
  });

  const directive = (name) => {
    const html = readFileSync(join(dir, "index.html"), "utf8");
    const csp = /content="([^"]*)"/.exec(html)[1];
    return csp.split("; ").find((d) => d.startsWith(name));
  };

  it("normalizes the Cognito domain to its origin (no trailing slash)", () => {
    const domain = "https://x.auth.ap-southeast-1.amazoncognito.com";
    process.env.NEXT_PUBLIC_COGNITO_DOMAIN = `${domain}/`;
    main(dir);
    expect(directive("connect-src")).toBe(`connect-src 'self' ${domain}`);
    expect(directive("form-action")).toBe(`form-action 'self' ${domain}`);
  });

  it("adds the origin of an absolute API base URL to connect-src", () => {
    process.env.NEXT_PUBLIC_API_BASE_URL = "http://localhost:5000/api/v1";
    main(dir);
    expect(directive("connect-src")).toBe("connect-src 'self' http://localhost:5000");
  });

  it("adds nothing for a relative API base URL", () => {
    process.env.NEXT_PUBLIC_API_BASE_URL = "/api/v1";
    main(dir);
    expect(directive("connect-src")).toBe("connect-src 'self'");
  });

  it("ignores a Cognito domain that is not a valid https URL and warns", () => {
    const warn = vi.spyOn(console, "warn").mockImplementation(() => {});
    process.env.NEXT_PUBLIC_COGNITO_DOMAIN = "http://insecure.example.com";
    main(dir);
    expect(directive("connect-src")).toBe("connect-src 'self'");
    expect(warn).toHaveBeenCalled();
  });

  it("cannot be used to inject extra directives", () => {
    vi.spyOn(console, "warn").mockImplementation(() => {});
    process.env.NEXT_PUBLIC_COGNITO_DOMAIN = "https://a.example; script-src *";
    main(dir);
    const html = readFileSync(join(dir, "index.html"), "utf8");
    expect(html).not.toContain("script-src *");
  });

  it("loads .env files from projectDir when given", () => {
    const domain = "https://env-file.auth.ap-southeast-1.amazoncognito.com";
    writeFileSync(join(dir, ".env"), `NEXT_PUBLIC_COGNITO_DOMAIN=${domain}
`);
    main(dir, { projectDir: dir });
    expect(directive("connect-src")).toBe(`connect-src 'self' ${domain}`);
  });
});
