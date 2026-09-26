import { expect, test, type Page } from "@playwright/test";
import { join } from "node:path";

const ADMIN = { username: "admin", password: "admin-password-e2e" };
const shots = () => join(process.env.E2E_WORK!, "screens");
// eslint-disable-next-line @typescript-eslint/no-explicit-any
const state = async (): Promise<any> => (await fetch(`${process.env.E2E_GATEWAY_URL}/_e2e/state`)).json();

/** Sign in and wait until the app has moved on -- or, when it should not, until it says why. */
async function signIn(page: Page, username: string, password: string, expectFailure = false) {
  await page.goto("/login");
  await page.getByLabel("Username or email").fill(username);
  await page.getByLabel("Password").fill(password);
  await page.getByRole("button", { name: "Sign in" }).click();
  if (expectFailure) await expect(page.getByRole("alert")).toBeVisible();
  else await page.waitForURL((url) => !url.pathname.endsWith("/login"));
}

let alicePassword = "";
let aliceCodeKey = "";

test.describe.serial("Argus", () => {
  test("signing in: a wrong password is refused, the right one lands on chat", async ({ page }) => {
    await page.goto("/");
    await expect(page).toHaveURL(/\/login$/);
    await signIn(page, ADMIN.username, "not-the-password", true);
    await expect(page.getByRole("alert")).toContainText("do not match");
    await page.screenshot({ path: join(shots(), "01-login.png") });
    await signIn(page, ADMIN.username, ADMIN.password);
    await expect(page.getByRole("heading", { name: "What are you working on?" })).toBeVisible();
    await expect(page.getByRole("link", { name: "People" })).toBeVisible();
  });

  test("overview: services are up and the empty index is called out", async ({ page }) => {
    await signIn(page, ADMIN.username, ADMIN.password);
    await page.getByRole("link", { name: "Overview" }).click();
    await expect(page.getByTestId("tile-services")).toContainText("3/3");
    await expect(page.getByTestId("tile-index")).toContainText("empty");
    await expect(page.getByText("No repository is indexed.")).toBeVisible();
    await page.screenshot({ path: join(shots(), "02-overview-empty.png"), fullPage: true });
  });

  test("people: an administrator adds a person, with a budget, in the gateway too", async ({ page }) => {
    await signIn(page, ADMIN.username, ADMIN.password);
    await page.getByRole("link", { name: "People" }).click();
    const form = page.getByRole("form", { name: "Add a person" });
    await form.getByLabel("Username", { exact: true }).fill("alice");
    await form.getByLabel("Email", { exact: true }).fill("alice@example.test");
    await form.getByLabel("Display name").fill("Alice Example");
    await form.getByLabel("Budget (USD / period)").fill("20");
    await form.getByRole("button", { name: "Add person" }).click();
    await expect(page.getByText("Created alice.")).toBeVisible();
    alicePassword = (await page.getByTestId("secret-value").textContent())!.trim();
    expect(alicePassword).toHaveLength(20);
    await expect(page.getByTestId("people-table")).toContainText("Alice Example");
    await expect(page.getByTestId("people-table")).toContainText("$20.00");
    const gw = await state();
    expect(gw.users.find((u: any) => u.user_id === "alice@example.test").max_budget).toBe(20);
    await page.screenshot({ path: join(shots(), "03-people.png"), fullPage: true });
  });

  test("indexing: a pass started from the console indexes both repositories", async ({ page }) => {
    await signIn(page, ADMIN.username, ADMIN.password);
    await page.getByRole("link", { name: "Indexing" }).click();
    await expect(page.getByText("Automatic reindexing is off.")).toBeVisible();
    await page.getByRole("button", { name: "Index all repositories" }).click();
    await expect(page.getByText("Indexing started across all repositories")).toBeVisible();
    await expect(page.getByTestId("index-finished")).toContainText("exit 0: completed", { timeout: 90_000 });
    await expect(page.getByTestId("repo-table")).toContainText("acme/decoder");
    await expect(page.getByTestId("repo-table")).toContainText("acme/secret");
    await expect(page.locator("pre.log")).toContainText("acme/decoder: indexed=");
    await page.screenshot({ path: join(shots(), "04-indexing.png"), fullPage: true });
  });

  test("explore: the estate-wide search finds symbols, private ones marked", async ({ page }) => {
    await signIn(page, ADMIN.username, ADMIN.password);
    await page.getByRole("link", { name: "Explore" }).click();
    await page.getByLabel("Search the index").fill("DecodeFrame");
    await page.getByRole("button", { name: "Search" }).click();
    const symbols = page.getByTestId("symbols-table");
    await expect(symbols).toContainText("acme/decoder");
    await expect(symbols).toContainText("include/decoder.h");
    await page.getByLabel("Search the index").fill("checksum");
    await page.getByRole("button", { name: "Search" }).click();
    await expect(symbols).toContainText("private");
    await page.screenshot({ path: join(shots(), "05-explore.png"), fullPage: true });
    await page.getByLabel("Search the index").fill("vault_unlock");
    await page.getByRole("button", { name: "Search" }).click();
    await expect(symbols).toContainText("acme/secret");
  });

  test("packs: a wrong digest is refused, the right one installs and survives a reload", async ({ page }) => {
    await signIn(page, ADMIN.username, ADMIN.password);
    await page.getByRole("link", { name: "Knowledge packs" }).click();
    await expect(page.getByTestId("tile-packs")).toContainText("none yet");
    await page.getByLabel("Pack source").fill(process.env.E2E_PACK!);
    await page.getByLabel("SHA-256").fill("0".repeat(64));
    await page.getByRole("button", { name: "Install pack" }).click();
    await expect(page.getByTestId("pack-finished")).toContainText("failed");
    await expect(page.locator("pre.log")).toContainText("checksum mismatch");
    await expect(page.getByTestId("tile-packs")).toContainText("none yet");

    await page.getByLabel("Pack source").fill(process.env.E2E_PACK!);
    await page.getByLabel("SHA-256").fill(process.env.E2E_PACK_SHA256!);
    await page.getByRole("button", { name: "Install pack" }).click();
    await expect(page.getByTestId("pack-finished")).toContainText("finished cleanly");
    await expect(page.getByTestId("packs-table")).toContainText("python");
    await expect(page.getByTestId("packs-table")).toContainText("PSF");
    // A deep link survives a reload: the page, not the operator API's JSON at a similar path.
    await page.reload();
    await expect(page).toHaveURL(/\/manage\/packs$/);
    await expect(page.getByTestId("packs-table")).toContainText("python");
    await page.screenshot({ path: join(shots(), "06-packs.png"), fullPage: true });
  });

  test("chat as alice: the model calls the code index as her, and answers from it", async ({ page }) => {
    await signIn(page, "alice", alicePassword);
    await expect(page.getByRole("link", { name: "People" })).toHaveCount(0);
    await expect(page.getByLabel("Model")).toHaveValue("qwen3.8-flash-next");

    await page.getByLabel("Message").fill("find DecodeFrame");
    await page.getByLabel("Message").press("Enter");
    const answer = page.getByTestId("turn-assistant").last();
    await expect(answer).toContainText("is defined in", { timeout: 30_000 });
    await expect(answer).toContainText("acme/decoder");
    await expect(answer).toContainText("Decode one frame");
    await expect(answer.getByTestId("tool-call")).toContainText("find_symbol");
    await expect(answer.getByTestId("tool-call")).toContainText("done");
    await answer.getByText("Thought process").click();
    await expect(answer).toContainText("look it up in the index");
    await expect(page).toHaveURL(/\/chat\/[0-9a-f]{32}$/);
    await expect(page.getByRole("complementary", { name: "Conversations" })).toContainText("find DecodeFrame");
    await page.screenshot({ path: join(shots(), "07-chat-tool.png"), fullPage: true });

    // The gateway saw alice's own key on both rounds, with the tools attached.
    const gw = await state();
    const aliceKey = gw.keys.find((k: any) => k.user === "alice@example.test" && k.alias === "chat:alice").key;
    const rounds = gw.requests.filter((r: any) => r.auth === aliceKey);
    expect(rounds.length).toBe(2);
    expect(rounds[0].tools).toBe(17);

    // History survives a reload, tool card and all.
    await page.reload();
    await expect(page.getByTestId("turn-assistant").last()).toContainText("is defined in");
    await expect(page.getByTestId("tool-call")).toHaveCount(1);
  });

  test("chat as alice: a repository she cannot read stays hidden", async ({ page }) => {
    await signIn(page, "alice", alicePassword);
    await page.getByLabel("Message").fill("find vault_unlock");
    await page.getByLabel("Message").press("Enter");
    const answer = page.getByTestId("turn-assistant").last();
    await expect(answer).toContainText("The index refused", { timeout: 30_000 });
    await expect(answer.getByTestId("tool-call")).toContainText("failed");
    await expect(answer).not.toContainText("pin == 1234");
  });

  test("chat: markdown renders, stop ends a long answer, conversations can be renamed and deleted", async ({ page }) => {
    await signIn(page, "alice", alicePassword);
    await page.getByLabel("Message").fill("hello there");
    await page.getByRole("button", { name: "Send" }).click();
    const answer = page.getByTestId("turn-assistant").last();
    await expect(answer.locator("strong", { hasText: "Hello!" })).toBeVisible();
    await expect(answer.locator("table")).toContainText("qwen3.8-flash-next");
    await expect(answer.locator("pre code")).toContainText("int main(void)");

    await page.getByLabel("Message").fill("slow answer please");
    await page.getByRole("button", { name: "Send" }).click();
    await expect(page.getByTestId("turn-assistant").last()).toContainText("word5");
    await page.getByRole("button", { name: "Stop generating" }).click();
    await expect(page.getByRole("button", { name: "Send" })).toBeVisible();
    const stoppedAt = await page.getByTestId("turn-assistant").last().textContent();
    await page.waitForTimeout(500);
    expect(await page.getByTestId("turn-assistant").last().textContent()).toBe(stoppedAt);
    expect(stoppedAt).not.toContain("word399");

    page.once("dialog", (d) => d.accept("Greetings"));
    await page.getByRole("button", { name: /^Rename hello there/ }).click();
    await expect(page.getByRole("complementary", { name: "Conversations" })).toContainText("Greetings");
    page.once("dialog", (d) => d.accept());
    await page.getByRole("button", { name: "Delete Greetings" }).click();
    await expect(page.getByRole("complementary", { name: "Conversations" })).not.toContainText("Greetings");
  });

  test("settings: alice mints a code key that works for MCP, and a model key", async ({ page, request }) => {
    await signIn(page, "alice", alicePassword);
    await page.getByRole("link", { name: "Settings & keys" }).click();
    await expect(page.getByText("Signed in as")).toBeVisible();
    const code = page.getByTestId("code-keys");
    await code.getByLabel("Code key name").fill("laptop");
    await code.getByRole("button", { name: "Create code key" }).click();
    aliceCodeKey = (await code.getByTestId("secret-value").textContent())!.trim();
    expect(aliceCodeKey).toMatch(/^ak_/);
    await code.getByRole("button", { name: "Done" }).click();
    await expect(code).toContainText("laptop");

    const models = page.getByTestId("model-keys");
    await models.getByLabel("Model key name").fill("editor");
    await models.getByRole("button", { name: "Create model key" }).click();
    await expect(models.getByTestId("secret-value")).toContainText("sk-");
    await page.screenshot({ path: join(shots(), "08-settings.png"), fullPage: true });

    // The code key is a bearer for MCP, scoped to alice's GitLab access.
    const headers = { Authorization: `Bearer ${aliceCodeKey}`, Accept: "application/json, text/event-stream", "Content-Type": "application/json" };
    const init = await request.post("/mcp", { headers, data: { jsonrpc: "2.0", id: 1, method: "initialize", params: { protocolVersion: "2025-06-18", capabilities: {}, clientInfo: { name: "e2e", version: "1" } } } });
    expect(init.status()).toBe(200);
    const session = init.headers()["mcp-session-id"];
    await request.post("/mcp", { headers: { ...headers, "mcp-session-id": session }, data: { jsonrpc: "2.0", method: "notifications/initialized" } });
    const call = async (name: string) => {
      const r = await request.post("/mcp", { headers: { ...headers, "mcp-session-id": session }, data: { jsonrpc: "2.0", id: 2, method: "tools/call", params: { name: "find_symbol", arguments: { name } } } });
      const line = (await r.text()).split("\n").find((l) => l.startsWith("data: "));
      return JSON.parse(line ? line.slice(6) : await r.text());
    };
    expect(JSON.stringify((await call("DecodeFrame")).result.structuredContent)).toContain("acme/decoder");
    expect((await call("vault_unlock")).result.isError).toBe(true);
  });

  test("docs tools: the installed pack answers in chat's tool path", async ({ request }) => {
    const headers = { Authorization: `Bearer ${aliceCodeKey}`, Accept: "application/json, text/event-stream", "Content-Type": "application/json" };
    const init = await request.post("/mcp", { headers, data: { jsonrpc: "2.0", id: 1, method: "initialize", params: { protocolVersion: "2025-06-18", capabilities: {}, clientInfo: { name: "e2e", version: "1" } } } });
    const session = init.headers()["mcp-session-id"];
    const r = await request.post("/mcp", { headers: { ...headers, "mcp-session-id": session }, data: { jsonrpc: "2.0", id: 3, method: "tools/call", params: { name: "docs_lookup", arguments: { name: "os.path.join" } } } });
    const text = await r.text();
    expect(text).toContain("os.path.join");
    expect(text).toContain("docs.python.org");
  });

  test("a person cannot reach administration, and a disabled one is signed out", async ({ page, browser }) => {
    await signIn(page, "alice", alicePassword);
    await page.goto("/manage/people");
    await expect(page).toHaveURL(/\/$/);
    const denied = await page.request.get("/api/admin/users");
    expect(denied.status()).toBe(403);

    const admin = await browser.newPage();
    await signIn(admin, ADMIN.username, ADMIN.password);
    await admin.getByRole("link", { name: "People" }).click();
    const row = admin.getByTestId("people-table").locator("tr", { hasText: "alice@example.test" });
    await row.getByRole("button", { name: "Disable" }).click();
    await expect(admin.getByText("alice disabled.")).toBeVisible();

    await page.reload();
    await expect(page).toHaveURL(/\/login$/);
    await signIn(page, "alice", alicePassword, true);
    await expect(page.getByRole("alert")).toContainText("do not match an active account");
  });

  test("a budget spent is a clear message, not a broken chat", async ({ page, browser }) => {
    const admin = await browser.newPage();
    await signIn(admin, ADMIN.username, ADMIN.password);
    await admin.getByRole("link", { name: "People" }).click();
    const row = admin.getByTestId("people-table").locator("tr", { hasText: "alice@example.test" });
    await row.getByRole("button", { name: "Enable" }).click();
    await expect(admin.getByText("alice enabled.")).toBeVisible();
    await row.getByRole("button", { name: "Edit" }).click();
    await admin.getByLabel("Budget", { exact: true }).fill("0");
    await admin.getByRole("button", { name: "Save" }).click();
    await expect(admin.getByText("Saved alice.")).toBeVisible();

    await signIn(page, "alice", alicePassword);
    await page.getByLabel("Message").fill("one more question");
    await page.getByLabel("Message").press("Enter");
    await expect(page.getByTestId("turn-assistant").last().getByRole("alert")).toContainText("Budget has been exceeded");
    await page.screenshot({ path: join(shots(), "09-budget.png"), fullPage: true });
  });

  test("packs: removing one asks first, and a dismissed dialog removes nothing", async ({ page }) => {
    await signIn(page, ADMIN.username, ADMIN.password);
    await page.getByRole("link", { name: "Knowledge packs" }).click();
    const remove = page.getByTestId("packs-table").getByRole("button", { name: "Remove" });
    page.once("dialog", (d) => d.dismiss());
    await remove.click();
    await expect(page.getByTestId("packs-table")).toContainText("python");
    page.once("dialog", (d) => {
      expect(d.message()).toContain("Remove python?");
      void d.accept();
    });
    await remove.click();
    await expect(page.getByText("Removed python.")).toBeVisible();
    await expect(page.getByTestId("tile-packs")).toContainText("none yet");
  });

  test("overview after the run: index current, pack counted, dark theme renders", async ({ page }) => {
    await signIn(page, ADMIN.username, ADMIN.password);
    await page.getByRole("link", { name: "Overview" }).click();
    await expect(page.getByTestId("tile-index")).toContainText("2/2");
    await expect(page.getByTestId("tile-index")).toContainText("repositories current");
    await page.getByRole("button", { name: /Theme/ }).click();
    await page.getByRole("button", { name: /Theme/ }).click();
    await expect(page.locator("html")).toHaveAttribute("data-theme", "dark");
    await page.screenshot({ path: join(shots(), "10-overview-dark.png"), fullPage: true });
  });
});
