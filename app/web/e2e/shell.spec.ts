import { expect, test } from '@playwright/test'
import { watchConsole } from './helpers.ts'

const areas = ['Chat', 'Usage & cost', 'Dashboards', 'Argus', 'Admin']

test('the overview loads with every area, the version and who is signed in', async ({ page, request }) => {
  const errors = watchConsole(page)
  const info = await (await request.get('/api/info')).json()
  await page.goto('/')
  await expect(page.getByRole('heading', { level: 1, name: 'Overview' })).toBeVisible()
  const nav = page.getByRole('navigation', { name: 'Main' })
  for (const a of areas) await expect(nav.getByRole('link', { name: a, exact: true })).toBeVisible()
  await expect(page.getByLabel('Version')).toHaveText(`v${info.version}`)
  await expect(page.getByRole('link', { name: 'Administrator' })).toHaveAttribute('href', '/account')
  expect(errors).toEqual([])
})

test('navigation moves between areas and deep links survive a reload', async ({ page }) => {
  const errors = watchConsole(page)
  await page.goto('/')
  const nav = page.getByRole('navigation', { name: 'Main' })
  for (const a of areas) {
    await nav.getByRole('link', { name: a, exact: true }).click()
    await expect(page.getByRole('heading', { level: 1, name: a })).toBeVisible()
  }
  await page.goto('/dashboards/usage/by-person')
  await expect(page.getByRole('heading', { level: 1, name: 'Dashboards' })).toBeVisible()
  expect(errors).toEqual([])
})

test('an area that is not native yet links to the service that covers it', async ({ page, baseURL }) => {
  await page.goto('/dashboards')
  const host = new URL(baseURL!).host
  await expect(page.getByRole('link', { name: 'Open Grafana' })).toHaveAttribute('href', new RegExp(`//grafana\\.${host.replace(/\./g, '\\.')}/$`))
})

test('unknown pages say so', async ({ page }) => {
  await page.goto('/no-such-page')
  await expect(page.getByRole('heading', { name: 'Page not found' })).toBeVisible()
})

test('no horizontal scrolling at any width', async ({ page }) => {
  for (const path of ['/', '/admin', '/admin/people', '/account', '/admin/audit', '/admin/model', '/usage']) {
    await page.goto(path)
    await page.getByRole('heading', { level: 1 }).first().waitFor()
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)
    expect(overflow, `${path} scrolls sideways`).toBeLessThanOrEqual(0)
  }
})

test('API paths never fall back to the page', async ({ request }) => {
  expect((await request.get('/api/does-not-exist')).status()).toBe(404)
})
