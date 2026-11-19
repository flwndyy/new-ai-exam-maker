// Fails when src/lib/api/schema.d.ts is out of sync with docs/api/openapi.yaml.
// Used by CI: `npm run api:check`.

import { execFileSync } from "node:child_process";
import { mkdtempSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";

const root = fileURLToPath(new URL("..", import.meta.url));
const spec = join(root, "..", "docs", "api", "openapi.yaml");
const committed = join(root, "src", "lib", "api", "schema.d.ts");
const cli = join(root, "node_modules", "openapi-typescript", "bin", "cli.js");

const dir = mkdtempSync(join(tmpdir(), "api-check-"));
const generated = join(dir, "schema.d.ts");

try {
  execFileSync(process.execPath, [cli, spec, "-o", generated], { stdio: "pipe" });
  const normalize = (file) => readFileSync(file, "utf8").replace(/\r\n/g, "\n");
  if (normalize(generated) !== normalize(committed)) {
    console.error("src/lib/api/schema.d.ts is out of date. Run `npm run api:generate` and commit the result.");
    // Set exitCode instead of calling process.exit() so the `finally` cleanup still runs.
    process.exitCode = 1;
  } else {
    console.log("API types are in sync with docs/api/openapi.yaml.");
  }
} finally {
  rmSync(dir, { recursive: true, force: true });
}
