import { expect, test } from '@playwright/test'
import { expectAccessible, screenshot, watchConsole } from './helpers.ts'

const headers = { 'X-Requested-With': 'e2e' }
// With a real model behind the gateway (the deployed stack): E2E_CHAT=1.
const live = process.env.E2E_CHAT === '1'

test('a workflow from a template runs: a JSON list, a step for each item, and the replay of its steps', async ({ page }, info) => {
  test.skip(!live, 'needs the deployed stack (E2E_CHAT=1)')
  test.setTimeout(300_000)
  const errors = watchConsole(page)
  let workflow: string | null = null
  try {
    await page.goto('/studio')
    await page.getByRole('list', { name: 'Templates' }).getByRole('button', { name: /One by one/ }).click()
    await expect(page).toHaveURL(/\/studio\/[0-9a-f-]+$/)
    workflow = page.url().split('/').pop() ?? null
    const canvas = page.getByRole('region', { name: 'The workflow' })
    await expect(canvas.getByRole('button', { name: /For each/ })).toBeVisible()
    await screenshot(page, info, 'studio-canvas')
    await expectAccessible(page, info, 'studio')

    await page.getByRole('button', { name: /Run now/ }).click()
    await page.getByRole('dialog').getByLabel('Words to start from (optional)').fill('A web app')
    await page.getByRole('dialog').getByRole('button', { name: /Run it/ }).click()
    const run = page.getByRole('region', { name: 'The run' })
    await expect(run.getByText('Done', { exact: true })).toBeVisible({ timeout: 240_000 })
    await expect(run.getByText(/^Step \d+ of \d+$/)).toBeVisible()
    await screenshot(page, info, 'studio-run')
    await run.getByRole('radio', { name: 'Timeline' }).click()
    await expect(run.getByRole('list', { name: 'Its steps' }).getByRole('listitem').first()).toBeVisible()
    expect(errors).toEqual([])
  } finally {
    if (workflow) await page.request.delete(`/api/studio/${workflow}`, { headers })
  }
})
