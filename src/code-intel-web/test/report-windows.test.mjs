// Every report of code-intel's own takes the shared time window (components/TimeWindowPicker), kept in the address, and asks
// the API for it: the overview's and people's spend, and one's own on Settings.
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { test } from "node:test";

const page = (path) => readFileSync(new URL(`../src/pages/${path}`, import.meta.url), "utf8");

for (const [path, api] of [["admin/Overview.tsx", "/api/admin/overview"], ["admin/People.tsx", "/api/admin/users"], ["Settings.tsx", "/api/me/usage"]]) {
  test(`${path} shows the time window and asks ${api} for it`, () => {
    const source = page(path);
    assert.match(source, /<TimeWindowPicker value=\{time\.window\} onChange=\{time\.set\} \/>/);
    assert.match(source, new RegExp(`"${api.replaceAll("/", "\\/")}" \\+ time\\.query`));
  });
}
