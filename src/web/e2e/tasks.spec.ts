import { expect, test } from '@playwright/test'
import { expectAccessible, screenshot, watchConsole } from './helpers.ts'

const live = process.env.E2E_CHAT === '1'

test('a scheduled task is set up in words, runs now, and its answer is a chat and a notification', async ({ page, isMobile }, info) => {
  test.setTimeout(240_000)
  const errors = watchConsole(page)
  const name = `Digest ${isMobile ? 'phone' : 'desk'} ${Date.now() % 100000}`
  await page.goto('/tasks')
  await expect(page.getByRole('heading', { level: 1, name: 'Scheduled tasks' })).toBeVisible()
  await page.getByRole('button', { name: 'New task' }).first().click()
  const dialog = page.getByRole('dialog', { name: 'New scheduled task' })
  await dialog.getByRole('textbox', { name: 'Name' }).fill(name)
  await dialog.getByRole('textbox', { name: 'What to ask' }).fill('Reply with only this, exactly: scheduled hello')
  await dialog.getByRole('combobox', { name: 'Repeat', exact: true }).click()
  await page.getByRole('option', { name: 'Once a week' }).click()
  await dialog.getByLabel('At', { exact: true }).fill('07:30')
  await dialog.getByRole('combobox', { name: 'On', exact: true }).click()
  await page.getByRole('option', { name: 'Monday' }).click()
  await dialog.getByRole('combobox', { name: 'Time zone' }).fill('Europe/Berlin')
  await expect(dialog.getByText('Mondays at 07:30.')).toBeVisible()
  await expectAccessible(page, info, 'task-form')
  await dialog.getByRole('button', { name: 'Add task' }).click()

  const card = page.getByRole('region', { name })
  await expect(card).toContainText('Mondays at 07:30 · Europe/Berlin')
  await expect(card).toContainText('Next:')
  await expectAccessible(page, info, 'tasks')
  await screenshot(page, info, `tasks${isMobile ? '-phone' : ''}`)

  if (live) {
    await card.getByRole('button', { name: 'Run now' }).click()
    await expect(card.getByText('Done', { exact: true })).toBeVisible({ timeout: 180_000 })
    await card.getByRole('link', { name: 'Open the chat' }).click()
    await expect(page.getByRole('region', { name: 'Answer' }).last()).toContainText(/scheduled hello/i)
    const bell = page.getByRole('button', { name: /^Notifications, \d+ new$/ })
    await expect(bell).toBeVisible({ timeout: 70_000 })
    await bell.click()
    await expect(page.getByRole('dialog', { name: 'Notifications' })).toContainText(name)
    await page.keyboard.press('Escape')
    // The run's chat goes, as the test's own.
    const chat = page.url().split('/').pop()!
    expect((await page.request.delete(`/api/chat/conversations/${chat}`, { headers: { 'X-Requested-With': 'e2e' } })).status()).toBe(204)
    await page.goto('/tasks')
  }

  await page.getByRole('region', { name }).getByRole('button', { name: 'Remove' }).click()
  await page.getByRole('alertdialog').getByRole('button', { name: 'Remove' }).click()
  await expect(page.getByRole('region', { name })).toHaveCount(0)
  expect(errors).toEqual([])
})
