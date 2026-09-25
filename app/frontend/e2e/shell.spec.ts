import { expect, test } from '@playwright/test'
import { expectAccessible, expectTheme, withTheme, screenshot, watchConsole } from './helpers.ts'

test('home: signed in, the admin sees the system summary, no console errors', async ({ page }, info) => {
  const errors = watchConsole(page)
  await page.goto('/')
  await expect(page.getByRole('heading', { level: 1, name: /Good (morning|afternoon|evening)/ })).toBeVisible()
  await expect(page.getByText('Your credit')).toBeVisible()
  await expect(page.getByRole('heading', { name: 'System' })).toBeVisible()
  await expect(page.getByText(/All \d+ up|\d+ down/)).toBeVisible()
  await screenshot(page, info, 'home-light')
  expect(errors).toEqual([])
})

for (const theme of ['light', 'dark'] as const) {
  for (const path of ['/', '/account', '/design', '/chat', '/no-such-page']) {
    test(`${path} is accessible in the ${theme} theme`, async ({ page }, info) => {
      const errors = watchConsole(page)
      await withTheme(page, theme)
      await page.goto(path)
      await expectTheme(page, theme)
      await page.getByRole('heading', { level: 1 }).first().waitFor()
      await expectAccessible(page, info, `${path}-${theme}`)
      await screenshot(page, info, `${path.replace(/\//g, '_') || 'home'}-${theme}`)
      expect(errors).toEqual([])
    })
  }
}

test('the command palette goes to a page', async ({ page, isMobile }) => {
  await page.goto('/')
  // The shortcut exists once the shell is up (after it knows who is signed in).
  await page.getByRole('button', { name: 'Search and commands' }).waitFor()
  if (isMobile) await page.getByRole('button', { name: 'Search and commands' }).click()
  else await page.keyboard.press('Control+K')
  await page.getByPlaceholder('Go to a page or run a command…').fill('account')
  // The best match is highlighted first: the page named so, not one that merely lists it as a keyword.
  await expect(page.getByRole('option', { name: 'Your account', selected: true })).toBeVisible()
  await page.keyboard.press('Enter')
  await expect(page).toHaveURL(/\/account$/)
  await expect(page.getByRole('heading', { level: 1, name: 'Your account' })).toBeVisible()
})

test('navigation: the sidebar on desktop, a drawer on the phone', async ({ page, isMobile }) => {
  await page.goto('/')
  if (isMobile) {
    await expect(page.getByRole('complementary', { name: 'Sidebar' })).toBeHidden()
    await page.getByRole('button', { name: 'Open navigation' }).click()
    await page.getByRole('dialog').getByRole('link', { name: 'Your account' }).or(page.getByRole('dialog').getByRole('link', { name: 'Home' })).first().waitFor()
    await page.getByRole('dialog').getByRole('link', { name: 'Dashboards' }).click()
    await expect(page.getByRole('dialog')).toBeHidden()
  } else {
    const sidebar = page.getByRole('complementary', { name: 'Sidebar' })
    await sidebar.getByRole('link', { name: 'Dashboards' }).click()
    await page.getByRole('button', { name: 'Collapse sidebar' }).click()
    await expect(sidebar.getByRole('link', { name: 'Dashboards' })).toBeVisible()
    await page.reload()
    await expect(page.getByRole('button', { name: 'Expand sidebar' })).toBeVisible()
    await page.getByRole('button', { name: 'Expand sidebar' }).click()
  }
  await expect(page).toHaveURL(/\/admin\/dashboards$/)
  await expect(page.getByRole('heading', { level: 1, name: 'Dashboards' })).toBeVisible()
})

test('no horizontal scrolling at any width', async ({ page }) => {
  for (const path of ['/', '/account', '/design', '/admin']) {
    await page.goto(path)
    await page.getByRole('heading', { level: 1 }).first().waitFor()
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)
    expect(overflow, path).toBeLessThanOrEqual(0)
  }
})

test('the theme follows the choice, before the first paint', async ({ page }) => {
  await page.goto('/account')
  await page.getByRole('radio', { name: 'Dark' }).click()
  await expect(page.locator('html')).toHaveClass(/dark/)
  await page.reload()
  // theme-init.js runs before React: the class is there at load.
  await expect(page.locator('html')).toHaveClass(/dark/)
  await page.getByRole('radio', { name: 'Light' }).click()
  await expect(page.locator('html')).not.toHaveClass(/dark/)
})
