import { expect, test } from '@playwright/test'
import { expectAccessible, screenshot, watchConsole } from './helpers.ts'

const live = process.env.E2E_CHAT === '1'
const H = { 'X-Requested-With': 'e2e' }

test('the science lab works a problem as a notebook, its check proved by SymPy, and lists it with its checks', async ({ page, isMobile }, info) => {
  test.setTimeout(300_000)
  const errors = watchConsole(page)
  await page.goto('/lab')
  await expect(page.getByRole('heading', { level: 1, name: 'Science lab' })).toBeVisible()
  const fields = await (await page.request.get('/api/lab/fields')).json()
  if (!fields.available) {
    // No sandbox (or no Python for this person): the page says why, and offers no problem to work.
    await expect(page.getByText(fields.why)).toBeVisible()
    await expect(page.getByRole('button', { name: 'Work it' })).toHaveCount(0)
    await expectAccessible(page, info, 'lab-unavailable')
    test.skip(true, `the lab is not available here: ${fields.why}`)
  }
  await expect(page.getByRole('radio', { name: /Mathematics/ })).toBeChecked()
  await expectAccessible(page, info, 'lab')
  await screenshot(page, info, `lab${isMobile ? '-phone' : ''}`)
  test.skip(!live, 'working a problem needs the deployed stack (E2E_CHAT=1)')

  await page.getByRole('textbox', { name: 'The problem' }).fill('Check with math_check that sin(x)**2 + cos(x)**2 equals 1, then say the result in one line.')
  await page.getByRole('button', { name: 'Work it' }).click()
  await expect(page).toHaveURL(/\/lab\/[0-9a-f-]{36}$/)
  const id = page.url().split('/').pop()!
  try {
    await expect(page.getByRole('region', { name: 'The problem' })).toContainText('sin(x)**2 + cos(x)**2')
    const check = page.getByRole('region', { name: 'Check' }).first()
    await expect(check).toContainText('Proved', { timeout: 240_000 })
    await expect(check).toContainText('simplifies to 0')
    await expect(page.getByRole('main').getByText(/\d+ proved/).first()).toBeVisible()
    await expect(page.getByRole('list', { name: 'Notebook' }).getByText('Working…')).toHaveCount(0, { timeout: 120_000 })
    await expectAccessible(page, info, 'lab-notebook')
    await screenshot(page, info, `lab-notebook${isMobile ? '-phone' : ''}`)

    // In the lab's list with its check counted; not among the chats.
    await page.goto('/lab')
    const row = page.getByRole('list', { name: 'Notebooks' }).locator(`a[href="/lab/${id}"]`)
    await expect(row).toContainText(/\d+ proved/)
    const chats = (await (await page.request.get('/api/chat/conversations')).json()) as { id: string }[]
    expect(chats.some((c) => c.id === id)).toBe(false)
  } finally {
    expect((await page.request.delete(`/api/chat/conversations/${id}`, { headers: H })).status()).toBe(204)
  }
  expect(errors).toEqual([])
})
