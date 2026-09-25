import { expect, test, type Page } from '@playwright/test'
import { expectAccessible, expectTheme, withTheme, noGateway, screenshot, watchConsole } from './helpers.ts'

const pages: [string, string][] = [
  ['/usage', 'Usage & cost'],
  ['/admin', 'Overview'],
  ['/admin/people', 'People'],
  ['/admin/groups', 'Groups'],
  ['/admin/tools', 'Tools'],
  ['/admin/sign-in', 'Sign-in'],
  ['/admin/model', 'Model'],
  ['/admin/settings', 'Settings'],
  ['/admin/audit', 'Audit log'],
  ['/admin/indexing', 'Indexing'],
  ['/admin/packs', 'Knowledge packs'],
  ['/admin/explore', 'Explore the index'],
  ['/admin/monitoring', 'Monitoring'],
]

/** The page has its heading and nothing is still loading. */
async function settled(page: Page, heading: string) {
  await expect(page.getByRole('heading', { level: 1, name: heading })).toBeVisible()
  await expect(page.locator('main [aria-busy="true"], main .animate-pulse')).toHaveCount(0, { timeout: 30_000 })
}

for (const theme of ['light', 'dark'] as const) {
  for (const [path, heading] of pages) {
    test(`${path} is accessible in the ${theme} theme`, async ({ page }, info) => {
      const errors = watchConsole(page)
      await withTheme(page, theme)
      await page.goto(path)
      await expectTheme(page, theme)
      await settled(page, heading)
      await expectAccessible(page, info, `${path}-${theme}`)
      await screenshot(page, info, `${path.replace(/\//g, '_')}-${theme}`)
      const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)
      expect(overflow, 'horizontal scrolling').toBeLessThanOrEqual(0)
      expect(errors).toEqual([])
    })
  }
}

test('the usage dashboard draws every panel without an error', async ({ page }) => {
  const errors = watchConsole(page)
  await page.goto('/usage')
  await expect(page.getByRole('tab', { name: 'Everyone' })).toHaveAttribute('aria-selected', 'true')
  await expect(page.getByRole('region', { name: 'Cost by person' })).toBeVisible()
  await expect(page.locator('main .animate-pulse')).toHaveCount(0, { timeout: 30_000 })
  const panels = page.locator('main [data-panel]')
  expect(await panels.count()).toBeGreaterThanOrEqual(20)
  await expect(panels.locator('[role=alert]')).toHaveCount(0)
  expect(errors).toEqual([])
})

test('a chart has a table view, and the time range changes what is asked', async ({ page }) => {
  await page.goto('/usage')
  const panel = page.getByRole('region', { name: 'Tokens by kind' }).first()
  await expect(panel).toBeVisible()
  await expect(page.locator('main .animate-pulse')).toHaveCount(0, { timeout: 30_000 })
  const toggle = panel.getByRole('button', { name: 'Show as table' })
  if (await toggle.count()) {
    await toggle.click()
    await expect(panel.getByRole('table')).toBeVisible()
    await panel.getByRole('button', { name: 'Show chart' }).click()
    await expect(panel.getByRole('img', { name: 'Tokens by kind' })).toBeVisible()
  } else {
    await expect(panel.getByText('No data in this time range.')).toBeVisible()
  }
  const request = page.waitForRequest((r) => r.url().includes('/panels/') && r.method() === 'POST')
  await page.getByRole('radio', { name: '7 days' }).first().click()
  const body = (await request).postDataJSON()
  expect(Object.keys(body).sort()).toEqual(['from', 'intervalMs', 'to'])
  expect(new Date(body.to).getTime() - new Date(body.from).getTime()).toBeGreaterThan(6.9 * 86400000)
})

test('my own usage', async ({ page }) => {
  await page.goto('/usage')
  await page.getByRole('tab', { name: 'Mine' }).click()
  await expect(page.getByLabel('Input, cache hit', { exact: true })).toBeVisible()
  await expect(page.getByLabel('Cost', { exact: true })).toHaveText(/^\$/)
})

test('a person, from added to deleted', async ({ page }) => {
  test.skip(noGateway, 'people get gateway accounts')
  const name = `e2e${Date.now().toString(36)}`
  await page.goto('/admin/people')
  await page.getByRole('button', { name: 'Add person' }).click()
  const dialog = page.getByRole('dialog')
  await dialog.getByLabel('Username').fill(name)
  await dialog.getByLabel('Email').fill(`${name}@example.test`)
  await dialog.getByLabel('Credit ($)').fill('5')
  await dialog.getByRole('button', { name: 'Add person' }).click()
  await expect(dialog.getByText('Person added')).toBeVisible()
  await dialog.getByRole('button', { name: 'Done' }).click()

  await page.getByRole('searchbox', { name: 'Search people' }).fill(name)
  await page.getByRole('cell', { name: new RegExp(name) }).click()
  await expect(page.getByRole('heading', { level: 1 })).toHaveText(name)
  await expect(page.getByText('/ $5.00')).toBeVisible()

  await page.getByRole('button', { name: 'Disable' }).click()
  await page.getByRole('alertdialog').getByRole('button', { name: 'Disable' }).click()
  await expect(page.getByText('Disabled', { exact: true }).first()).toBeVisible()
  await page.getByRole('button', { name: 'Enable' }).click()
  await expect(page.getByRole('button', { name: 'Disable' })).toBeVisible()

  await page.getByRole('button', { name: `Delete ${name}` }).click()
  await page.getByRole('dialog').getByLabel('Username').fill(name)
  await page.getByRole('dialog').getByRole('button', { name: 'Delete' }).click()
  await expect(page).toHaveURL(/\/admin\/people$/)
})

test('a live setting applies at once and is audited; a stack setting waits and can be discarded', async ({ page }) => {
  const brand = `E2E Brand ${Date.now().toString(36)}`
  await page.goto('/admin/settings')
  await settled(page, 'Settings')
  await page.getByLabel('Product name').fill(brand)
  await page.getByRole('region', { name: 'Unsaved changes' }).getByRole('button', { name: 'Save changes' }).click()
  await expect(page.getByText('1 in effect now')).toBeVisible()
  // The name (from /api/info) is in use without a reload: the tab title and the sidebar.
  await expect(page).toHaveTitle(`Settings · ${brand}`)
  await page.getByRole('button', { name: /Back to the default/ }).first().click()
  await expect(page.getByLabel('Product name')).toHaveValue('LLM Service')

  if (await page.getByText('Stack settings cannot be saved yet').isVisible()) return
  await page.goto('/admin/settings#backup')
  const keep = page.getByLabel('Backups kept')
  const was = await keep.inputValue()
  await keep.fill(String(Number(was || '14') + 1))
  await page.getByRole('region', { name: 'Unsaved changes' }).getByRole('button', { name: 'Save changes' }).click()
  await expect(page.getByText(/\.env changes? (is|are) waiting to be applied/)).toBeVisible()
  await expect(page.getByRole('region', { name: 'apply command', exact: true })).toHaveText('./scripts/apply-settings.sh')
  await page.getByRole('button', { name: /Discard pending change/ }).click()
  await expect(page.getByText(/\.env changes? (is|are) waiting to be applied/)).toBeHidden()

  await page.goto('/admin/audit')
  await page.getByRole('radio', { name: 'Settings' }).click()
  await expect(page.getByRole('cell', { name: 'Branding:ProductName' }).first()).toBeVisible()
})

test('members cannot open admin pages', async ({ browser, baseURL }) => {
  test.skip(noGateway, 'makes a person')
  const admin = await browser.newContext({ ignoreHTTPSErrors: true, baseURL, storageState: 'e2e/.auth/state.json' })
  const name = `e2em${Date.now().toString(36)}`
  const made = await admin.request.post('/api/admin/people', { data: { userName: name, email: `${name}@example.test` }, headers: { 'X-Requested-With': 'e2e' } })
  const { id, password } = await made.json()
  const ctx = await browser.newContext({ ignoreHTTPSErrors: true, baseURL, storageState: { cookies: [], origins: [] } })
  const page = await ctx.newPage()
  await page.goto('/login')
  await page.getByLabel('Username or email').fill(name)
  await page.getByLabel('Password').fill(password)
  await page.getByRole('button', { name: 'Sign in' }).click()
  await expect(page).not.toHaveURL(/\/login/)
  await expect(page.getByRole('button', { name: /Account menu/ })).toBeVisible()
  await expect(page.getByRole('navigation', { name: 'Main' }).getByRole('link', { name: 'People' })).toHaveCount(0)
  await page.goto('/admin/settings')
  await expect(page.getByRole('heading', { name: 'Admins only' })).toBeVisible()
  await ctx.close()
  await admin.request.delete(`/api/admin/people/${id}`, { headers: { 'X-Requested-With': 'e2e' } })
  await admin.close()
})
