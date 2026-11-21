import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { test } from "node:test";
import vm from "node:vm";

const source = await readFile(new URL("./index-html-rewrite.js", import.meta.url), "utf8");
const context = {};
vm.runInNewContext(`${source}\nthis.cloudFrontHandler = handler;`, context);

function rewrite(uri) {
  const request = { uri };
  return context.cloudFrontHandler({ request }).uri;
}

test("rewrites the root document to index.html", () => {
  assert.equal(rewrite("/"), "/index.html");
});

test("rewrites extensionless routes to their exported index document", () => {
  assert.equal(rewrite("/dashboard"), "/dashboard/index.html");
  assert.equal(rewrite("/exams/view.v2/foo"), "/exams/view.v2/foo/index.html");
});

test("keeps exported files unchanged", () => {
  assert.equal(rewrite("/assets/app.js"), "/assets/app.js");
  assert.equal(rewrite("/dashboard/index.html"), "/dashboard/index.html");
});

test("keeps API requests unchanged for the /api/* behavior", () => {
  assert.equal(rewrite("/api/health"), "/api/health");
  assert.equal(rewrite("/api/"), "/api/");
});
