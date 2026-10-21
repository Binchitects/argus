import { expect, test } from '@playwright/test'
import { expectAccessible, screenshot, watchConsole } from './helpers.ts'

const headers = { 'X-Requested-With': 'e2e' }
// A PostgreSQL database with a shop schema (shop.orders, shop.customers) and a role that may only read it:
// E2E_DATA_CONNECTION=Host=…;Database=…;Username=…;Password=…  With a real model behind the gateway: E2E_CHAT=1.
const connection = process.env.E2E_DATA_CONNECTION
const live = process.env.E2E_CHAT === '1'

test('a question about the shop is answered with its SQL, a chart and the rows, and pinned to a board', async ({ page, isMobile }, info) => {
  test.skip(!connection || !live, 'needs a shop database (E2E_DATA_CONNECTION) and the deployed stack (E2E_CHAT=1)')
  test.setTimeout(300_000)
  const errors = watchConsole(page)
  const name = `Shop ${isMobile ? 'phone' : 'desk'} ${Math.random().toString(36).slice(2, 8)}`
  const made = await page.request.post('/api/admin/data-sources', { data: { name, connection, tables: ['shop.customers', 'shop.orders'], notes: 'Totals are in euros.', reach: 'company' }, headers })
  expect(made.status()).toBe(201)
  const source = (await made.json()).id as string
  let board: string | null = null
  try {
    await page.goto('/data')
    await page.getByRole('combobox', { name: 'Database' }).click()
    await page.getByRole('option', { name }).click()
    await page.getByLabel(`Ask ${name}`).fill('Total sales per country, biggest first')
    await page.getByRole('button', { name: 'Ask', exact: true }).click()
    const answer = page.getByRole('list', { name: 'Answers' }).getByRole('listitem').first()
    // The chart (its SVG inside is an image too).
    await expect(answer.getByRole('img').first()).toBeVisible({ timeout: 180_000 })
    await answer.getByText('SQL', { exact: true }).click()
    await expect(answer.getByLabel('The SQL')).toHaveValue(/shop\.orders/i)
    await screenshot(page, info, 'data-answer')
    await expectAccessible(page, info, 'data')
    await answer.getByRole('radio', { name: 'Table' }).click()
    await expect(answer.getByRole('table')).toBeVisible()

    await answer.getByRole('button', { name: /Pin to a board/ }).click()
    const dialog = page.getByRole('dialog', { name: 'Pin to a board' })
    await dialog.getByRole('combobox', { name: 'Board' }).click()
    await page.getByRole('option', { name: 'A new board…' }).click()
    await dialog.getByLabel("The new board's name").fill(`Board ${name}`)
    await dialog.getByRole('button', { name: 'Pin' }).click()
    // Pinned: the dialog says so, with the board's link.
    await page.getByRole('dialog', { name: 'Pinned' }).getByRole('link', { name: 'Open the board' }).click()
    await expect(page.getByRole('heading', { level: 1, name: `Board ${name}` })).toBeVisible()
    board = page.url().split('/').pop() ?? null
    await expect(page.getByRole('list', { name: 'Charts' }).getByRole('listitem')).toHaveCount(1)
    await screenshot(page, info, 'data-board')
    expect(errors).toEqual([])
  } finally {
    if (board) await page.request.delete(`/api/data/boards/${board}`, { headers })
    await page.request.delete(`/api/admin/data-sources/${source}`, { headers })
  }
})
