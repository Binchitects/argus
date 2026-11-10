// The Indexing page's counts (components/index-counts.ts), made JavaScript by the TypeScript the build uses.
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { test } from "node:test";
import ts from "typescript";

const source = readFileSync(new URL("../src/components/index-counts.ts", import.meta.url), "utf8");
const { outputText } = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 } });
const { indexCounts } = await import(`data:text/javascript,${encodeURIComponent(outputText)}`);

test("it says repositories, with the branches beside them, and the symbols", () => {
  assert.equal(indexCounts({ repos: 12, branches: 30, symbols: 4812 }), "12 repositories, 30 branches, 4,812 symbols");
  assert.equal(indexCounts({ repos: 1, branches: 1, symbols: 1 }), "1 repository, 1 branch, 1 symbol");
});

test("a count the summary lacks is unknown, not the rows on screen", () => {
  assert.equal(indexCounts({ branches: 3, symbols: 10 }), "? repositories, 3 branches, 10 symbols");
  assert.equal(indexCounts({}), "? repositories, ? branches, ? symbols");
});

test("the page takes its heading from these counts, with no fallback to the rows", () => {
  const page = readFileSync(new URL("../src/pages/admin/Indexing.tsx", import.meta.url), "utf8");
  assert.match(page, /indexCounts\(s\.index\)/);
  assert.doesNotMatch(page, /refs/);
  assert.doesNotMatch(page, /s\.index\.repos \?\? s\.repos\.length/);
});
