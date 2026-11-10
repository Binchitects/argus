// Every table's sort and filter (components/table-rows.ts), made JavaScript by the TypeScript the build uses.
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { test } from "node:test";
import ts from "typescript";

const source = readFileSync(new URL("../src/components/table-rows.ts", import.meta.url), "utf8");
const { outputText } = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 } });
const { cellValue, tableRows } = await import(`data:text/javascript,${encodeURIComponent(outputText)}`);

const rows = [
  { repo: "acme/b10", symbols: 10, last: null },
  { repo: "acme/b2", symbols: 200, last: 5 },
  { repo: "tools/a", symbols: 3, last: 9 },
];
const columns = { repo: (r) => r.repo, symbols: (r) => r.symbols, last: (r) => r.last };
const names = (list) => list.map((r) => r.repo);

test("text sorts as people read it, numbers as numbers, either way", () => {
  assert.deepEqual(names(tableRows(rows, columns, "", { by: "repo", desc: false })), ["acme/b2", "acme/b10", "tools/a"]);
  assert.deepEqual(names(tableRows(rows, columns, "", { by: "symbols", desc: true })), ["acme/b2", "acme/b10", "tools/a"]);
  assert.deepEqual(names(tableRows(rows, columns, "", { by: "symbols", desc: false })), ["tools/a", "acme/b10", "acme/b2"]);
});

test("empty values go last whichever way it sorts", () => {
  assert.equal(names(tableRows(rows, columns, "", { by: "last", desc: false })).at(-1), "acme/b10");
  assert.equal(names(tableRows(rows, columns, "", { by: "last", desc: true })).at(-1), "acme/b10");
});

test("the filter keeps rows holding the words in any column, and leaves the rows given alone", () => {
  assert.deepEqual(names(tableRows(rows, columns, "ACME", null)), ["acme/b10", "acme/b2"]);
  assert.deepEqual(names(tableRows(rows, columns, "200", null)), ["acme/b2"]);
  tableRows(rows, columns, "", { by: "repo", desc: true });
  assert.deepEqual(names(rows), ["acme/b10", "acme/b2", "tools/a"]);
});

test("a cell reads as a number when all of it is one", () => {
  assert.deepEqual(["1,204", "35%", "$4.20", "−3", "v2", " ok "].map(cellValue), [1204, 35, 4.2, -3, "v2", "ok"]);
});
