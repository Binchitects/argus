import { expect, test } from '@playwright/test'
import { expectAccessible, noObserve } from './helpers.ts'

// Beyond each page's axe check (admin.spec, shell.spec): what opens on a page,
// the keyboard's way through it, and pages at 320 px (WCAG 1.4.10, reflow).

const opens: [string, string | RegExp][] = [
  ['/admin/people', 'Add person'],
  ['/admin/groups', 'New group'],
  ['/admin/tools', 'Add MCP server'],
  ['/admin/models', 'Add a model'],
  ['/admin/models', 'Add a server'],
  ['/admin/packs', 'Add a pack'],
  ['/chat', 'Chat settings'],
  ['/chat', /^Tools/],
  ['/', 'Search and commands'],
]

for (const [path, name] of opens) {
  test(`${path}: "${name}" opens accessible, takes focus, and gives it back to its button`, async ({ page }, info) => {
    await page.goto(path)
    await page.getByRole('heading', { level: 1 }).first().waitFor()
    await expect(page.locator('main [aria-busy="true"], main .animate-pulse')).toHaveCount(0, { timeout: 30_000 })
    const button = page.getByRole('button', { name, exact: typeof name === 'string' }).first()
    await expect(button).toBeEnabled()
    await button.click()
    const opened = page.locator('[role="dialog"], [role="alertdialog"]').last()
    await expect(opened).toBeVisible()
    await expect.poll(() => opened.evaluate((el) => el.contains(document.activeElement))).toBe(true)
    await expectAccessible(page, info, `${path}-${String(name)}`)
    await page.keyboard.press('Escape')
    await expect(opened).toBeHidden()
    await expect(button).toBeFocused()
  })
}

test('the skip link takes the keyboard past the navigation to the page', async ({ page, isMobile }) => {
  test.skip(isMobile, 'a keyboard')
  await page.goto('/admin/people')
  await page.getByRole('heading', { level: 1 }).waitFor()
  await page.keyboard.press('Tab')
  const skip = page.getByRole('link', { name: 'Skip to content' })
  await expect(skip).toBeFocused()
  await expect(skip).toBeVisible()
  await page.keyboard.press('Enter')
  await expect(page.locator('main')).toBeFocused()
  // The next stop is in the page, not the sidebar.
  await page.keyboard.press('Tab')
  expect(await page.evaluate(() => !!document.activeElement?.closest('main'))).toBe(true)
})

const pages = ['/', '/account', '/setup', '/chat', '/usage', '/admin', '/admin/people', '/admin/groups', '/admin/tools', '/admin/sign-in', '/admin/models', '/admin/model',
  '/admin/settings', '/admin/audit', '/admin/indexing', '/admin/packs', '/admin/explore', '/admin/monitoring', '/admin/dashboards', '/admin/dashboards/stack-health',
  ...(noObserve ? [] : ['/admin/logs', '/admin/alerts'])]

test('every page fits 320 px without scrolling sideways', async ({ page, isMobile }) => {
  test.skip(isMobile, 'once is enough')
  test.setTimeout(180_000)
  await page.setViewportSize({ width: 320, height: 640 })
  const wide: string[] = []
  for (const path of pages) {
    await page.goto(path)
    await page.getByRole('heading', { level: 1 }).first().waitFor()
    await expect(page.locator('main [aria-busy="true"], main .animate-pulse')).toHaveCount(0, { timeout: 30_000 })
    const over = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)
    if (over > 0) wide.push(`${path} (${over}px)`)
  }
  expect(wide).toEqual([])
})
