import { expect, test, type Page } from '@playwright/test'
import { expectAccessible, expectTheme, withTheme, noGateway, noObserve, screenshot, watchConsole } from './helpers.ts'

const pages: [string, string][] = [
  ['/usage', 'Usage & cost'],
  ['/admin', 'Overview'],
  ['/admin/people', 'People'],
  ['/admin/groups', 'Groups'],
  ['/admin/tools', 'Tools'],
  ['/admin/sign-in', 'Sign-in'],
  ['/admin/models', 'Models'],
  ['/admin/model', 'Deployment'],
  ['/admin/settings', 'Settings'],
  ['/admin/audit', 'Audit log'],
  ['/admin/indexing', 'Indexing'],
  ['/admin/packs', 'Knowledge packs'],
  ['/admin/explore', 'Explore the index'],
  ['/admin/monitoring', 'Monitoring'],
  ['/admin/dashboards', 'Dashboards'],
  ['/admin/dashboards/stack-health', 'Stack Health & Alerts'],
  ['/admin/dashboards/stack-logs', 'Logs (Loki)'],
  ['/admin/dashboards/gpu-hardware', 'GPU Hardware'],
  // Their data comes from Loki and Alertmanager, which CI does not run.
  ...(noObserve ? [] : ([['/admin/logs', 'Logs'], ['/admin/alerts', 'Alerts']] as [string, string][])),
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

test('every dashboard draws every panel without an error', async ({ page }) => {
  test.skip(noObserve, 'the metric and log panels read Prometheus and Loki')
  const errors = watchConsole(page)
  await page.goto('/admin/dashboards')
  const links = page.getByRole('list', { name: 'Dashboards' }).getByRole('link')
  await expect(links.first()).toBeVisible()
  const hrefs = await links.evaluateAll((a) => a.map((x) => x.getAttribute('href')!))
  expect(hrefs.length).toBe(9)
  for (const href of hrefs) {
    await page.goto(href)
    await expect(page.locator('main [data-panel]').first()).toBeVisible()
    await expect(page.locator('main .animate-pulse')).toHaveCount(0, { timeout: 30_000 })
    await expect(page.locator('main [data-panel] [role=alert]'), href).toHaveCount(0)
  }
  expect(errors).toEqual([])
})

test('logs: narrowed by container, level and text in the address, and live', async ({ page }) => {
  test.skip(noObserve, 'the logs come from Loki')
  await page.goto('/admin/logs')
  const lines = page.getByRole('region', { name: 'Logs, log lines' })
  await expect(lines.getByRole('listitem').first()).toBeVisible({ timeout: 30_000 })

  await page.getByRole('button', { name: 'Containers' }).click()
  await page.getByRole('menuitemcheckbox', { name: 'traefik', exact: true }).click()
  await page.keyboard.press('Escape')
  await expect(page).toHaveURL(/container=traefik/)
  await expect(page.getByText('The query sent to Loki')).toBeVisible()
  await page.getByText('The query sent to Loki').click()
  await expect(page.getByText('{container=~"traefik"}')).toBeVisible()
  // A long query scrolls in its box; the page never gets wider than the screen.
  expect(await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)).toBeLessThanOrEqual(0)

  await page.getByRole('radio', { name: 'Warnings and errors' }).click()
  await expect(page).toHaveURL(/level=warn/)
  await expect(page.getByText(/detected_level=~/)).toBeVisible()

  await page.getByLabel('Contains').fill('no-such-text-in-any-log')
  await expect(page).toHaveURL(/q=no-such-text/)
  await expect(page.getByText('No log lines in this time range.')).toBeVisible()

  await page.getByRole('button', { name: 'Live' }).click()
  await expect(page.getByRole('button', { name: 'Live' })).toHaveAttribute('aria-pressed', 'true')
  const tail = await page.waitForRequest((r) => r.url().includes('/api/admin/logs/?') && r.url().includes('limit=500'), { timeout: 10_000 })
  expect(new URL(tail.url()).searchParams.get('container')).toBe('traefik')
})

test('alerts: what fires, what fired, and every rule with its query', async ({ page }) => {
  test.skip(noObserve, 'the alerts come from Alertmanager and Prometheus')
  await page.goto('/admin/alerts')
  await expect(page.getByText('Firing now', { exact: true }).first()).toBeVisible()
  const rules = page.getByRole('heading', { name: 'stack', exact: true })
  await expect(rules).toBeVisible()
  const first = page.locator('details summary').first()
  await first.click()
  await expect(page.locator('details[open]').getByRole('region', { name: 'PromQL' })).toBeVisible()
  expect(await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)).toBeLessThanOrEqual(0)
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

test('the engine has its model loaded, and more come from the library', async ({ page }) => {
  await page.goto('/admin/models')
  await settled(page, 'Models')
  // Read-only: loading another model here would switch it for every other test.
  test.skip(await page.getByText(/needs the llama\.cpp engine/).isVisible(), 'no llama.cpp engine')
  const env = page.locator('main section').filter({ has: page.getByText('.env', { exact: true }) })
  await expect(env.getByText('Loaded', { exact: true })).toBeVisible({ timeout: 60_000 })
  await expect(env.getByRole('button', { name: 'Unload' })).toBeVisible()
  await page.getByRole('button', { name: 'Add a model' }).click()
  const dialog = page.getByRole('dialog', { name: 'Add a model' })
  await dialog.getByRole('combobox', { name: 'Model file' }).click()
  // Choosing a file names the model after it.
  await page.getByRole('option').first().click()
  await expect(dialog.getByLabel('Name')).not.toHaveValue('')
  await dialog.getByRole('button', { name: 'Cancel' }).click()
  await expect(dialog).toBeHidden()
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
