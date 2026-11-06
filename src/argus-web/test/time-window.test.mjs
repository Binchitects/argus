// The reports' time window (components/time-window.ts), made JavaScript by the TypeScript the build uses.
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { test } from "node:test";
import ts from "typescript";

const source = readFileSync(new URL("../src/components/time-window.ts", import.meta.url), "utf8");
const { outputText } = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 } });
const { days, label, problem } = await import(`data:text/javascript,${encodeURIComponent(outputText)}`);

const now = new Date("2026-11-07T15:00:00Z");

test("a preset is the days it names, both ends in, in UTC", () => {
  assert.equal(days({ from: "", to: "" }, now), null);
  assert.deepEqual(days({ from: "now/d", to: "now/d" }, now), { from: "2026-11-07", to: "2026-11-07" });
  assert.deepEqual(days({ from: "now-7d", to: "now" }, now), { from: "2026-11-01", to: "2026-11-07" });
  assert.deepEqual(days({ from: "now/M", to: "now" }, now), { from: "2026-11-01", to: "2026-11-07" });
  assert.deepEqual(days({ from: "now-1M/M", to: "now-1M/M" }, now), { from: "2026-10-01", to: "2026-10-31" });
  assert.deepEqual(days({ from: "now-1M/M", to: "now-1M/M" }, new Date("2026-03-10T00:00:00Z")), { from: "2026-02-01", to: "2026-02-28" });
});

test("a custom range is its two days, and one the server cannot read is none", () => {
  assert.deepEqual(days({ from: "2026-10-03", to: "2026-10-09" }, now), { from: "2026-10-03", to: "2026-10-09" });
  assert.equal(days({ from: "2026-10-09", to: "2026-10-03" }, now), null);
  assert.equal(days({ from: "yesterday", to: "now" }, now), null);
});

test("it says why two days are no window, and names the window", () => {
  assert.equal(problem("", "2026-10-01"), "Choose both days.");
  assert.equal(problem("2026-10-02", "2026-10-01"), "The first day comes before the last.");
  assert.equal(problem("2024-01-01", "2026-10-01"), "A window is at most 400 days.");
  assert.equal(problem("2026-10-01", "2026-10-01"), null);
  assert.equal(label({ from: "now-1M/M", to: "now-1M/M" }), "Last month");
  assert.equal(label({ from: "2026-10-01", to: "2026-10-31" }), "2026-10-01 to 2026-10-31");
});
