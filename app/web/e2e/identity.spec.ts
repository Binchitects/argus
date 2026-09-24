import { expect, test } from '@playwright/test'
import { watchConsole } from './helpers.ts'

const fresh = { storageState: { cookies: [], origins: [] } }

test.describe('signed out', () => {
  test.use(fresh)

  test('a visitor is sent to sign in and lands where they were going', async ({ page }) => {
    const errors = watchConsole(page)
    await page.goto('/admin/audit')
    await expect(page).toHaveURL(/\/login\?rd=%2Fadmin%2Faudit$/)
    await page.getByLabel('Username or email').fill('admin')
    await page.getByLabel('Password').fill(process.env.E2E_PASSWORD!)
    await page.getByRole('button', { name: 'Sign in' }).click()
    await expect(page).toHaveURL(/\/admin\/audit$/)
    await expect(page.getByRole('heading', { name: 'Audit log' })).toBeVisible()
    expect(errors).toEqual([])
  })

  test('a wrong password is refused with a reason', async ({ page }) => {
    await page.goto('/login')
    // A new name each run: five misses on one name from one address ban it (by design).
    await page.getByLabel('Username or email').fill(`nobody-${Date.now()}-${Math.random().toString(36).slice(2, 6)}`)
    await page.getByLabel('Password').fill('not the password')
    await page.getByRole('button', { name: 'Sign in' }).click()
    await expect(page.getByRole('alert')).toHaveText('Wrong username or password.')
  })
})

test('an admin adds a person, who then signs in, is a member, and changes their password', async ({ page, browser, baseURL }) => {
  const name = 'e2e' + Math.random().toString(36).slice(2, 8)
  await page.goto('/admin/people')
  await page.getByRole('button', { name: 'Add a person' }).click()
  await page.getByLabel(/Username/).fill(name)
  await page.getByLabel('Email').fill(`${name}@example.test`)
  await page.getByLabel('Name', { exact: true }).fill(`E2E ${name}`)
  await page.getByRole('button', { name: 'Add' }).click()
  const password = (await page.getByLabel('Password', { exact: true }).textContent())!.trim()
  expect(password).toHaveLength(20)
  await page.getByRole('button', { name: 'Done' }).click()
  await expect(page.getByRole('link', { name: `E2E ${name}` })).toBeVisible()

  const ctx = await browser.newContext({ ...fresh, baseURL, ignoreHTTPSErrors: true })
  const person = await ctx.newPage()
  await person.goto('/login')
  await person.getByLabel('Username or email').fill(name)
  await person.getByLabel('Password').fill(password)
  await person.getByRole('button', { name: 'Sign in' }).click()
  await expect(person.getByRole('heading', { level: 1, name: 'Overview' })).toBeVisible()
  await expect(person.getByRole('navigation', { name: 'Main' }).getByRole('link', { name: 'Admin' })).toHaveCount(0)
  await person.goto('/admin')
  await expect(person.getByText('Only admins can open this page.')).toBeVisible()

  await person.goto('/account')
  await person.getByLabel('Current password').fill(password)
  await person.getByLabel('New password', { exact: true }).fill('orbit lantern maple thunder')
  await person.getByLabel('New password again').fill('orbit lantern maple thunder')
  await person.getByRole('button', { name: 'Change password' }).click()
  await expect(person.getByText('Password changed.')).toBeVisible()
  await ctx.close()

  // Clean up through the person's page.
  await page.getByRole('link', { name: `E2E ${name}` }).click()
  page.once('dialog', (d) => d.accept(name))
  await page.getByRole('button', { name: `Delete ${name}` }).click()
  await expect(page).toHaveURL(/\/admin\/people$/)
  await expect(page.getByRole('link', { name: `E2E ${name}` })).toHaveCount(0)
})

test('two-factor setup shows a QR code and a key to type', async ({ page }) => {
  await page.goto('/account')
  await page.getByRole('button', { name: 'Set up' }).click()
  await expect(page.getByRole('img', { name: 'QR code for your authenticator app' })).toBeVisible()
  await expect(page.getByText(/Or enter this key by hand/)).toBeVisible()
})
