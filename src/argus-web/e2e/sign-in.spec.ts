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

test("sign-in: the logo, then its eyes, play behind the form, which stays usable", async ({ page }) => {
  await page.goto("/login");
  const stage = page.locator(".eyes");
  await expect(stage).toHaveAttribute("aria-hidden", "true");
  await expect.poll(() => stage.locator(".eye").count()).toBeGreaterThan(30);
  const eyes = await stage.locator(".eye").count();
  expect(eyes).toBeLessThanOrEqual(110);
  expect(await eyesOverForm(page)).toBe(0);
  // The logo and every eye share one 12-second turn, started together: they keep in step.
  const turns = await page.evaluate(`(() => {
    const turns = document.getAnimations().filter((a) => a.effect && a.effect.getTiming().duration === 12000);
    return { count: turns.length, starts: new Set(turns.map((a) => a.startTime)).size, logo: turns.some((a) => a.effect.target.classList.contains("eyes-logo")) };
  })()`);
  expect(turns).toEqual({ count: eyes + 1, starts: 1, logo: true });
  // Nothing of it takes a click.
  await expect(stage).toHaveCSS("pointer-events", "none");
  await page.getByLabel("Password").click();
  await expect(page.getByLabel("Password")).toBeFocused();

  // A phone gets fewer eyes, none over the form either, and nothing scrolls sideways.
  await page.setViewportSize({ width: 390, height: 844 });
  await expect.poll(() => stage.locator(".eye").count()).toBeLessThanOrEqual(30);
  expect(await eyesOverForm(page)).toBe(0);
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
