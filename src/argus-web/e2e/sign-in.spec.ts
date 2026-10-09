import { expect, test, type Page } from "@playwright/test";

// The sign-in page's scene: Argus's logo, then its eyes opening across the screen
// behind the form, which stays readable and usable; with reduced motion, a still logo.
// (Code run in the page is given as text: these files are checked without the DOM's types.)

/** How many eyes overlap the form, as they are drawn now. */
async function eyesOverForm(page: Page) {
  const form = (await page.locator("form.card.login").boundingBox())!;
  let over = 0;
  for (const eye of await page.locator(".eyes .eye").all()) {
    const b = await eye.boundingBox();
    if (b && b.x < form.x + form.width && form.x < b.x + b.width && b.y < form.y + form.height && form.y < b.y + b.height) over++;
  }
  return over;
}

const sideways = "document.documentElement.scrollWidth - document.documentElement.clientWidth";

/** Each eye drawn now: where it is put and how big, and its box on the screen. */
const placed = `[...document.querySelectorAll(".eyes .eye")].map((e) => {
  const b = e.getBoundingClientRect();
  return { at: [e.style.left, e.style.top, e.style.width].join(" "), left: b.left, top: b.top, right: b.right, bottom: b.bottom };
})`;
type Placed = { at: string; left: number; top: number; right: number; bottom: number };

test("sign-in: the logo, then its eyes, play behind the form, which stays usable", async ({ page }) => {
  await page.goto("/login");
  const stage = page.locator(".eyes");
  await expect(stage).toHaveAttribute("aria-hidden", "true");
  await expect.poll(() => stage.locator(".eye").count()).toBeGreaterThan(30);
  const eyes = await stage.locator(".eye").count();
  expect(eyes).toBeLessThanOrEqual(130);
  await expect.poll(() => eyesOverForm(page)).toBe(0);
  // The logo and every eye share one 12-second turn, started together: they keep in step.
  // (The same start can read back a hair apart, as 118.466 and 118.46600000000001.)
  const turns = await page.evaluate(`(() => {
    const turns = document.getAnimations().filter((a) => a.effect && a.effect.getTiming().duration === 12000);
    const starts = turns.map((a) => a.startTime);
    return { count: turns.length, spread: Math.max(...starts) - Math.min(...starts), logo: turns.some((a) => a.effect.target.classList.contains("eyes-logo")) };
  })()`) as { count: number; spread: number; logo: boolean };
  expect(turns.count).toBe(eyes + 1);
  expect(turns.spread).toBeLessThan(1);
  expect(turns.logo).toBe(true);
  // Nothing of it takes a click.
  await expect(stage).toHaveCSS("pointer-events", "none");
  await page.getByLabel("Password").click();
  await expect(page.getByLabel("Password")).toBeFocused();

  // A wrong password grows the form by its message: only the eyes beside it go, every other stays put.
  const before = await page.evaluate(placed) as Placed[];
  await page.getByLabel("Username or email").fill("nobody");
  await page.getByLabel("Password").fill("not-a-password");
  await page.getByRole("button", { name: "Sign in" }).click();
  await expect(page.getByRole("alert")).toBeVisible();
  // They are laid out again on the next frame; those going fade out first.
  await page.waitForTimeout(600);
  await expect.poll(() => eyesOverForm(page)).toBe(0);
  const form = (await page.locator("form.card.login").boundingBox())!;
  const after = new Set((await page.evaluate(placed) as Placed[]).map((e) => e.at));
  const beside = (e: Placed) => e.right > form.x - 80 && e.left < form.x + form.width + 80 && e.bottom > form.y - 80 && e.top < form.y + form.height + 80;
  const away = before.filter((e) => !beside(e));
  expect(away.length).toBeGreaterThan(eyes / 2);
  expect(away.filter((e) => !after.has(e.at))).toEqual([]);

  // A phone gets fewer eyes, none over the form either, and nothing scrolls sideways.
  await page.setViewportSize({ width: 390, height: 844 });
  await expect.poll(() => stage.locator(".eye").count()).toBeLessThan(eyes / 2);
  await expect.poll(() => eyesOverForm(page)).toBe(0);
  expect(await page.evaluate(sideways)).toBeLessThanOrEqual(0);
});

test("sign-in: with reduced motion, only the logo, still", async ({ page }) => {
  await page.emulateMedia({ reducedMotion: "reduce" });
  await page.goto("/login");
  await expect(page.getByLabel("Username or email")).toBeVisible();
  await expect(page.locator(".eyes-logo")).toHaveCSS("opacity", "1");
  await page.waitForTimeout(300);
  expect(await page.locator(".eyes .eye").count()).toBe(0);
  expect(await page.evaluate("document.getAnimations().length")).toBe(0);
});
