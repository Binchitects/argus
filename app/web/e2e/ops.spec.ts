import { expect, test } from '@playwright/test'
import { watchConsole } from './helpers.ts'

test('the usage dashboard draws every panel without an error', async ({ page }) => {
  const errors = watchConsole(page)
  await page.goto('/usage')
  await expect(page.getByRole('tab', { name: 'Everyone' })).toHaveAttribute('aria-selected', 'true')
  const panels = page.locator('section.panel')
  await expect(page.getByRole('region', { name: 'Cost by person' })).toBeVisible()
  // Every query panel settles: no skeletons left, and none shows an error.
  await expect(page.locator('.skeleton')).toHaveCount(0, { timeout: 30_000 })
  expect(await panels.count()).toBeGreaterThanOrEqual(20)
  await expect(page.locator('section.panel [role=alert]')).toHaveCount(0)
  await expect(page.getByRole('region', { name: 'Input tokens, cache hit' })).toBeVisible()
  expect(errors).toEqual([])
})

test('a chart has a table view, and the time range changes what is asked', async ({ page }) => {
  await page.goto('/usage')
  const panel = page.getByRole('region', { name: 'Tokens by kind' })
  await expect(panel).toBeVisible()
  await expect(page.locator('.skeleton')).toHaveCount(0, { timeout: 30_000 })
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
  await page.getByRole('button', { name: 'Last 7 days' }).click()
  const body = (await request).postDataJSON()
  expect(Object.keys(body).sort()).toEqual(['from', 'intervalMs', 'to'])
  expect(new Date(body.to).getTime() - new Date(body.from).getTime()).toBeGreaterThan(6.9 * 86400000)
})

test('my own usage', async ({ page }) => {
  await page.goto('/usage')
  await page.getByRole('tab', { name: 'Mine' }).click()
  await expect(page.getByLabel('Input, cache hit')).toBeVisible()
  await expect(page.getByLabel('Cost', { exact: true })).toHaveText(/^\$/)
})

for (const [path, heading] of [
  ['/admin', 'Services'],
  ['/admin/model', 'Switch the deployment to'],
  ['/admin/monitoring', 'Elsewhere'],
  ['/admin/settings', 'Prices (per 1M tokens)'],
] as const) {
  test(`admin page ${path} loads`, async ({ page }) => {
    const errors = watchConsole(page)
    await page.goto(path)
    await expect(page.getByRole('heading', { name: heading })).toBeVisible()
    expect(errors).toEqual([])
  })
}

test('the model page has a block to paste for every shipped sample', async ({ page }) => {
  await page.goto('/admin/model')
  const samples = page.locator('details.sample')
  await expect(samples.first()).toBeVisible()
  await samples.first().locator('summary').click()
  await expect(samples.first().locator('pre')).toContainText('# >>> MODEL')
})

test('indexing, packs and explore either work or say Argus is not set up', async ({ page }) => {
  for (const path of ['/admin/indexing', '/admin/packs', '/admin/explore']) {
    await page.goto(path)
    await expect(page.getByText(/Argus is not set up|Index now|Installed knowledge packs|Search/).first()).toBeVisible()
    await expect(page.locator('[role=alert]')).toHaveCount(0)
  }
})
