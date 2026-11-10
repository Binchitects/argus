import { expect, test, type Page } from "@playwright/test";
import { join } from "node:path";

// After app.spec.ts (the files run in order, against one backend): both repositories are indexed.
const ADMIN = { username: "admin", password: "admin-password-e2e" };
const shots = () => join(process.env.E2E_WORK!, "screens");

async function repositories(page: Page) {
  await page.goto("/login");
  await page.getByLabel("Username or email").fill(ADMIN.username);
  await page.getByLabel("Password").fill(ADMIN.password);
  await page.getByRole("button", { name: "Sign in" }).click();
  await page.waitForURL((url) => !url.pathname.endsWith("/login"));
  await page.getByRole("link", { name: "Repositories" }).click();
  await expect(page.getByTestId("repos-table")).toContainText("decoder");
}

async function idle(page: Page) {
  await expect.poll(async () => {
    const view = await (await page.request.get("/admin/repos")).json();
    return view.running || view.pending.length > 0;
  }, { timeout: 90_000 }).toBe(false);
}

test.describe.serial("Repositories", () => {
  test("found by name or group, narrowed by state, updated in a batch with an outcome each, and their log read", async ({ page }) => {
    await repositories(page);
    const table = page.getByTestId("repos-table");
    await page.getByLabel("Find a repository").fill("secret");
    await expect(table).toContainText("secret");
    await expect(table).not.toContainText("decoder");
    await page.getByLabel("Find a repository").fill("acme");
    await expect(table).toContainText("decoder");
    await page.getByLabel("Status").selectOption("failed");
    await expect(page.getByText("No repositories match these filters.")).toBeVisible();
    await page.getByLabel("Status").selectOption("indexed");
    await expect(page.getByText("2 of 2 repositories.")).toBeVisible();
    await page.getByLabel("Group").selectOption("acme");
    await expect(page.getByText("2 of 2 repositories.")).toBeVisible();

    await page.getByLabel("Select all shown").check();
    const bulk = page.getByRole("region", { name: "Bulk actions" });
    await expect(bulk).toContainText("2 selected");
    page.once("dialog", (d) => {
      expect(d.message()).toContain("Update 2 repositories now");
      void d.accept();
    });
    await bulk.getByRole("button", { name: "Update now" }).click();
    const outcome = page.getByTestId("batch-outcome");
    await expect(outcome).toContainText("2 of 2 repositories done");
    await expect(outcome).toContainText("acme/decoder");
    await expect(outcome).toContainText("Updating now.");
    await idle(page);
    await page.reload();
    await expect(page.getByTestId("repos-table").getByTestId("repo-status").first()).toContainText("Indexed");

    await page.getByRole("button", { name: "Log of acme/decoder" }).click();
    const log = page.getByTestId("repo-log");
    await expect(log).toContainText("Run started by an admin.");
    await expect(log).toContainText("Fetched from GitLab");
    await expect(log).toContainText("main: up to date at");
    await expect(log).toContainText("Done in");
    // The first run, from the Indexing page, read it in full and embedded its symbols.
    await expect(log).toContainText("read for the first time");
    await expect(log).toContainText("for meaning search");
    await page.screenshot({ path: join(shots(), "11-repository-log.png"), fullPage: true });
    await page.getByRole("dialog").getByRole("button", { name: "Close" }).click();
    await page.screenshot({ path: join(shots(), "12-repositories.png"), fullPage: true });
  });

  test("a repository gets a schedule of its own, and the rest the schedule for all", async ({ page }) => {
    await repositories(page);
    await page.getByLabel("Select acme/secret").check();
    const bulk = page.getByRole("region", { name: "Bulk actions" });
    await bulk.getByLabel("Schedule for the selected").selectOption("daily");
    await bulk.getByLabel("At").fill("03:30");
    page.once("dialog", (d) => void d.accept());
    await bulk.getByRole("button", { name: "Set schedule" }).click();
    await expect(page.getByTestId("batch-outcome")).toContainText("Schedule: Every day at 03:30.");
    const secret = page.getByTestId("repos-table").locator("tr", { hasText: "secret" });
    await expect(secret).toContainText("Every day at 03:30");
    await expect(secret).toContainText(/next in \d+[mh]/);
    await bulk.getByRole("button", { name: "Clear" }).click();

    await page.getByRole("button", { name: "Change" }).click();
    await page.getByLabel("Schedule for all").selectOption("hours");
    await page.getByLabel("Every (hours)").fill("12");
    await page.getByLabel("Time zone").fill("Europe/Berlin");
    await page.getByRole("button", { name: "Save" }).click();
    await expect(page.getByText("Schedule for all saved.")).toBeVisible();
    const decoder = page.getByTestId("repos-table").locator("tr", { hasText: "decoder" });
    await expect(decoder).toContainText("Every 12 hours (for all)");
    await expect(secret).toContainText("Every day at 03:30");
    // A zone that does not exist is refused, with what to give.
    await page.getByRole("button", { name: "Change" }).click();
    await page.getByLabel("Time zone").fill("Mars/Base");
    await page.getByRole("button", { name: "Save" }).click();
    await expect(page.getByText(/is not a time zone/)).toBeVisible();
  });

  test("brought up again with another token on a GitLab set up anew: the same repositories, no second copy", async ({ page }) => {
    await repositories(page);
    // The same repositories, every one under a new project id.
    expect((await fetch(`${process.env.E2E_GITLAB_URL}/_e2e/renumber`, { method: "POST" })).ok).toBe(true);
    await page.getByRole("button", { name: "Refresh from GitLab" }).click();
    await expect(page.getByText("GitLab lists 2 repositories; 2 repositories found again under a new id, with their index.")).toBeVisible();
    await expect(page.getByText("2 of 2 repositories.")).toBeVisible();
    await expect(page.getByTestId("repos-table").locator("tr", { hasText: "secret" })).toContainText("Every day at 03:30");

    // Their index answers as before, and the next run finds nothing new to read.
    await page.getByRole("link", { name: "Explore" }).click();
    await page.getByLabel("Search the index").fill("DecodeFrame");
    await page.getByRole("button", { name: "Search" }).click();
    await expect(page.getByTestId("symbols-table")).toContainText("acme/decoder");
    await page.getByRole("link", { name: "Repositories" }).click();
    await page.getByRole("button", { name: "Update acme/decoder" }).click();
    await idle(page);
    await page.getByRole("button", { name: "Log of acme/decoder" }).click();
    await expect(page.getByTestId("repo-log").getByRole("region").first()).toContainText("main: up to date at");
  });
});
