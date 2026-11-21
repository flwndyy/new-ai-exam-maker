// Post-build step: injects a Content-Security-Policy <meta> into every exported HTML page.
// The static export is served by S3/CloudFront, so the policy cannot come from a response header here.
// Inline scripts emitted by Next are allowed by hash, so script-src needs no 'unsafe-inline'.
// Used by `npm run build` (runs after `next build`).

import { createHash } from "node:crypto";
import { readdirSync, readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

// @next/env is CommonJS, so the named export is only reachable through the default import.
import nextEnv from "@next/env";

const { loadEnvConfig } = nextEnv;

const SCRIPT_RE = /<script\b([^>]*)>([\s\S]*?)<\/script>/gi;
const SRC_ATTR_RE = /(^|\s)src\s*=/i;
const CSP_META_RE = /http-equiv\s*=\s*["']?Content-Security-Policy/i;
const CHARSET_META = '<meta charSet="utf-8"/>';

/** Returns `'sha256-<base64>'` source expressions for every inline (src-less) script. */
export function hashInlineScripts(html) {
  const hashes = [];
  for (const [, attrs, body] of html.matchAll(SCRIPT_RE)) {
    if (SRC_ATTR_RE.test(attrs)) continue;
    const digest = createHash("sha256").update(body, "utf8").digest("base64");
    hashes.push(`'sha256-${digest}'`);
  }
  return hashes;
}

export function buildCsp({ scriptHashes, cognitoDomain, apiOrigin }) {
  const sources = (...parts) => parts.filter(Boolean).join(" ");
  return [
    "default-src 'self'",
    `script-src ${sources("'self'", ...scriptHashes)}`,
    "style-src 'self' 'unsafe-inline'",
    "img-src 'self' data:",
    "font-src 'self'",
    `connect-src ${sources("'self'", cognitoDomain, apiOrigin)}`,
    "object-src 'none'",
    "base-uri 'self'",
    `form-action ${sources("'self'", cognitoDomain)}`,
  ].join("; ");
}

export function injectCsp(html, csp) {
  if (CSP_META_RE.test(html)) {
    throw new Error("HTML already contains a Content-Security-Policy meta tag");
  }
  const content = csp.replace(/&/g, "&amp;").replace(/"/g, "&quot;");
  const meta = `<meta http-equiv="Content-Security-Policy" content="${content}"/>`;
  // Function replacers avoid special `$` handling in the replacement text.
  if (html.includes(CHARSET_META)) {
    return html.replace(CHARSET_META, () => CHARSET_META + meta);
  }
  const head = /<head\b[^>]*>/i;
  if (!head.test(html)) {
    throw new Error("HTML has no <head> element to inject the CSP meta tag into");
  }
  return html.replace(head, (open) => open + meta);
}

function* htmlFiles(dir) {
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    const path = join(dir, entry.name);
    if (entry.isDirectory()) yield* htmlFiles(path);
    else if (entry.name.endsWith(".html")) yield path;
  }
}

function parseOrigin(value) {
  try {
    const { origin } = new URL(value);
    return origin === "null" ? undefined : origin; // opaque origins (mailto:, data:, ...) are unusable
  } catch {
    return undefined;
  }
}

// Relative values such as "/api/v1" are same-origin and already covered by 'self'.
function apiOriginFrom(value) {
  return value ? parseOrigin(value) : undefined;
}

// Reduced to a bare origin so a stray ';' or whitespace in the env value cannot add directives.
function cognitoDomainFrom(value) {
  if (!value) return undefined;
  const origin = parseOrigin(value);
  if (!origin || !origin.startsWith("https://")) {
    console.warn("NEXT_PUBLIC_COGNITO_DOMAIN is not a valid https URL; omitting it from the CSP.");
    return undefined;
  }
  return origin;
}

/**
 * Injects the CSP into every HTML file under `outDir`.
 * `projectDir` (optional) makes `.env*` files load the same way `next build` does.
 * Returns the origins that were added so the CLI can report them.
 */
export function main(outDir = "out", { projectDir } = {}) {
  if (projectDir) loadEnvConfig(projectDir);
  const cognitoDomain = cognitoDomainFrom(process.env.NEXT_PUBLIC_COGNITO_DOMAIN);
  const apiOrigin = apiOriginFrom(process.env.NEXT_PUBLIC_API_BASE_URL);
  let count = 0;
  for (const file of htmlFiles(outDir)) {
    try {
      const html = readFileSync(file, "utf8");
      const csp = buildCsp({ scriptHashes: hashInlineScripts(html), cognitoDomain, apiOrigin });
      writeFileSync(file, injectCsp(html, csp));
      count += 1;
    } catch (error) {
      throw new Error(`CSP injection failed for ${file}: ${error.message}`, { cause: error });
    }
  }
  console.log(`Injected Content-Security-Policy into ${count} HTML file(s) in ${outDir}.`);
  return { count, cognitoDomain, apiOrigin };
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  try {
    const { cognitoDomain, apiOrigin } = main(fileURLToPath(new URL("../out", import.meta.url)), {
      projectDir: process.cwd(),
    });
    console.log(`CSP connect-src extras: cognito=${cognitoDomain ?? "(none)"} api=${apiOrigin ?? "(none)"}`);
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}
