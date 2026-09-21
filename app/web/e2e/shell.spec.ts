import { expect, test, type Page } from '@playwright/test'

const areas = ['Chat', 'Usage & cost', 'Dashboards', 'Argus', 'Admin']

/** Fails the test on any console error, including Content-Security-Policy violations. */
function watchConsole(page: Page): string[] {
  const errors: string[] = []
  page.on('console', (m) => m.type() === 'error' && errors.push(m.text()))
  page.on('pageerror', (e) => errors.push(e.message))
  return errors
}

test('the overview loads with every area and the backend version', async ({ page, request }) => {
  const errors = watchConsole(page)
  const info = await (await request.get('/api/info')).json()
  await page.goto('/')
  await expect(page.getByRole('heading', { level: 1, name: 'Overview' })).toBeVisible()
  const nav = page.getByRole('navigation', { name: 'Main' })
  for (const a of areas) await expect(nav.getByRole('link', { name: a, exact: true })).toBeVisible()
  await expect(page.getByLabel('Version')).toHaveText(`v${info.version}`)
  expect(errors).toEqual([])
})

test('navigation moves between areas and deep links survive a reload', async ({ page }) => {
  const errors = watchConsole(page)
  await page.goto('/')
  const nav = page.getByRole('navigation', { name: 'Main' })
  for (const a of areas) {
    await nav.getByRole('link', { name: a, exact: true }).click()
    await expect(page.getByRole('heading', { level: 1, name: a })).toBeVisible()
    await expect(nav.getByRole('link', { name: a, exact: true })).toHaveAttribute('aria-current', 'page')
  }
  await page.goto('/dashboards/usage/by-person')
  await expect(page.getByRole('heading', { level: 1, name: 'Dashboards' })).toBeVisible()
  expect(errors).toEqual([])
})

test('an area that is not native yet links to the service that covers it', async ({ page, baseURL }) => {
  await page.goto('/chat')
  const host = new URL(baseURL!).host
  await expect(page.getByRole('link', { name: 'Open Open WebUI' })).toHaveAttribute('href', new RegExp(`//chat\\.${host.replace(/\./g, '\\.')}/$`))
})

test('unknown pages say so', async ({ page }) => {
  await page.goto('/no-such-page')
  await expect(page.getByRole('heading', { name: 'Page not found' })).toBeVisible()
})

test('no horizontal scrolling at any width', async ({ page }) => {
  for (const path of ['/', '/admin']) {
    await page.goto(path)
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)
    expect(overflow, `${path} scrolls sideways`).toBeLessThanOrEqual(0)
  }
})

test('API paths never fall back to the page', async ({ request }) => {
  const res = await request.get('/api/does-not-exist')
  expect(res.status()).toBe(404)
})

test('signed-out visitors are sent to sign in', async ({ browser, baseURL }) => {
  test.skip(!process.env.E2E_PASSWORD, 'only behind Authelia')
  const ctx = await browser.newContext({ ignoreHTTPSErrors: true, storageState: { cookies: [], origins: [] } })
  const res = await ctx.request.get(baseURL!, { maxRedirects: 0 })
  expect(res.status()).toBe(302)
  expect(res.headers()['location']).toMatch(/^https:\/\/auth\./)
  await ctx.close()
})
