import { expect, test } from '@playwright/test'
import { expectAccessible, screenshot, watchConsole } from './helpers.ts'

test.describe('signed out', () => {
  test.use({ storageState: { cookies: [], origins: [] } })

  test('a signed-out visit goes to sign-in and comes back after it', async ({ page }, info) => {
    const errors = watchConsole(page)
    await page.goto('/account')
    await expect(page).toHaveURL(/\/login\?rd=%2Faccount/)
    await expect(page.getByRole('heading', { name: 'Sign in' })).toBeVisible()
    await expectAccessible(page, info, 'login')
    await screenshot(page, info, 'login')
    await page.getByLabel('Username or email').fill(process.env.E2E_USER ?? 'admin')
    await page.getByLabel('Password').fill(process.env.E2E_PASSWORD!)
    await page.getByRole('button', { name: 'Sign in' }).click()
    await expect(page).toHaveURL(/\/account$/)
    await expect(page.getByRole('heading', { level: 1, name: 'Your account' })).toBeVisible()
    expect(errors).toEqual([])
  })

  test('a wrong password says so', async ({ page }) => {
    await page.goto('/login')
    await page.getByLabel('Username or email').fill('nobody-here')
    await page.getByLabel('Password').fill('definitely wrong')
    await page.getByRole('button', { name: 'Sign in' }).click()
    await expect(page.getByRole('alert')).toBeVisible()
    await expect(page).toHaveURL(/\/login/)
  })
})

test('sign out ends the session', async ({ browser, baseURL }) => {
  // Its own session, so the shared one stays signed in for the other tests.
  const ctx = await browser.newContext({ storageState: { cookies: [], origins: [] }, ignoreHTTPSErrors: true, baseURL })
  const page = await ctx.newPage()
  await page.goto('/login')
  await page.getByLabel('Username or email').fill(process.env.E2E_USER ?? 'admin')
  await page.getByLabel('Password').fill(process.env.E2E_PASSWORD!)
  await page.getByRole('button', { name: 'Sign in' }).click()
  await page.getByRole('button', { name: /Account menu/ }).click()
  await page.getByRole('menuitem', { name: 'Sign out' }).click()
  await expect(page).toHaveURL(/\/login/)
  await page.goto('/')
  await expect(page).toHaveURL(/\/login/)
  await ctx.close()
})

test('the page is served with its security headers, and API paths reach the API', async ({ request }) => {
  const page = await request.get('/')
  expect(page.headers()['content-security-policy']).toContain("script-src 'self'")
  expect(page.headers()['x-frame-options']).toBe('DENY')
  expect(page.headers()['cache-control']).toBe('no-cache')
  const info = await request.get('/api/info')
  expect(info.headers()['content-type']).toContain('application/json')
  const missing = await request.get('/assets/no-such-file.js')
  expect(missing.status()).toBe(404)
  expect(missing.headers()['cache-control'] ?? '').not.toContain('immutable')
})
