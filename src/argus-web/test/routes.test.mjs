// Every route of App.tsx has its help, and an address with none is not shown another page's.
// Plain Node (npm test): help.ts is made JavaScript by the TypeScript the build uses.
import assert from "node:assert/strict";
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { after, test } from "node:test";
import { pathToFileURL } from "node:url";
import ts from "typescript";

const read = (file) => readFileSync(new URL(`../src/${file}`, import.meta.url), "utf8");

/** help.ts as a module, its import of react-router pointed at the one installed here. */
async function loadHelp() {
  const { outputText } = ts.transpileModule(read("help.ts"), { compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 } });
  const dir = mkdtempSync(join(tmpdir(), "argus-help-"));
  after(() => rmSync(dir, { recursive: true, force: true }));
  const file = join(dir, "help.mjs");
  writeFileSync(file, outputText.replaceAll('from "react-router"', `from "${import.meta.resolve("react-router")}"`));
  return import(pathToFileURL(file).href);
}

const { help, helpFor, noHelp } = await loadHelp();

test("every route of App.tsx is one of its pages, and every page has its help", () => {
  const app = read("App.tsx");
  // The routes are made from the record of pages; only sign-in and the catch-all are written out.
  const paths = [...app.matchAll(/\bpath=(\{[^}]*\}|"[^"]*")/g)].map((m) => m[1]);
  assert.deepEqual(paths, ['{"/login" satisfies ArgusRoute}', "{path.slice(1)}", '"*"'], "a <Route> written by hand: make it one of App.tsx's pages, with its help in help.ts");
  assert.equal([...app.matchAll(/<Route\b[^>\n]*\bindex\b/g)].length, 1, "one index route, the record's");
  const record = app.slice(app.indexOf("const pages"), app.indexOf("};", app.indexOf("const pages")));
  const pages = [...record.matchAll(/^\s*"([^"]+)":/gm)].map((m) => m[1]);
  assert.deepEqual(pages.toSorted(), Object.keys(help).filter((r) => r !== "/login").toSorted());
  for (const [route, topic] of Object.entries(help)) {
    assert.ok(topic.title && topic.about, `${route}: a title and what the page is for`);
    for (const t of topic.tasks) assert.ok(t.steps.length > 0, `${route}: ${t.title} has its steps`);
  }
});

test("an address finds its page's help; one with none, or an admins' page for someone else, finds none", () => {
  assert.equal(helpFor("/", false), help["/"]);
  assert.equal(helpFor("/chat/42", false), help["/chat/:id"]);
  assert.equal(helpFor("/manage", true), help["/manage"]);
  assert.equal(helpFor("/manage/people", true), help["/manage/people"]);
  assert.equal(helpFor("/manage/people", false), noHelp);
  assert.equal(helpFor("/no/such/page", true), noHelp);
  assert.ok(!Object.values(help).includes(noHelp));
});
