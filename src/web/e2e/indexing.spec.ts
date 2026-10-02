import { expect, test, type Page } from '@playwright/test'
import { expectAccessible, screenshot, watchConsole } from './helpers.ts'

// Against the deployed Argus and its GitLab (the test GitLab's root/eal-core, which has a v2 branch).
async function argusUp(page: Page) {
  const status = await page.request.get('/api/admin/argus/status')
  const repos = status.ok() ? await (await page.request.get('/api/admin/argus/repos')).json() : null
  return (await status.json()).configured !== false && repos?.repos?.some((r: { repo: string }) => r.repo === 'root/eal-core')
}

async function waitIdle(page: Page) {
  await expect.poll(async () => (await (await page.request.get('/api/admin/argus/status')).json()).job.state, { timeout: 120_000 }).toBe('idle')
}

test('a repository gets a branch from GitLab, is updated, and shows each branch at its commit', async ({ page, isMobile }, info) => {
  test.skip(isMobile, 'one browser changes the index at a time')
  test.setTimeout(300_000)
  test.skip(!(await argusUp(page)), 'needs the deployed Argus with the test GitLab')
  const errors = watchConsole(page)
  await waitIdle(page)
  await page.goto('/admin/indexing')
  const repos = page.getByRole('region', { name: 'Repositories' })
  await repos.getByRole('searchbox').fill('eal-core')
  const row = repos.getByRole('row').filter({ hasText: 'root/eal-core' })
  await expect(row).toContainText('add src/decoder.c')

  async function setV2(on: boolean) {
    await row.getByRole('button', { name: 'Branches of root/eal-core' }).click()
    const dialog = page.getByRole('dialog', { name: 'Branches of root/eal-core' })
    await expect(dialog.getByRole('checkbox', { name: 'main' })).toBeDisabled()
    await expect(dialog).toContainText('release: hardware decode path')
    const v2 = dialog.getByRole('checkbox', { name: 'v2' })
    if (on) await v2.check()
    else await v2.uncheck()
    if (on) await expectAccessible(page, info, 'branches-dialog')
    await dialog.getByRole('button', { name: 'Save' }).click()
    await expect(dialog).toBeHidden()
    await row.getByRole('button', { name: 'Update the index of root/eal-core' }).click()
    await waitIdle(page)
    await page.reload()
    await repos.getByRole('searchbox').fill('eal-core')
  }

  try {
    await setV2(true)
    await expect(row).toContainText('release: hardware decode path', { timeout: 30_000 })
    await expect(row.getByText('v2', { exact: true }).first()).toBeVisible()
    await expectAccessible(page, info, 'indexing-repos')
    await screenshot(page, info, 'indexing-repos')
  } finally {
    await setV2(false)
  }
  await expect(row).not.toContainText('release: hardware decode path', { timeout: 30_000 })
  expect(errors).toEqual([])
})

test('the index schedule is set in words and shows the next passes', async ({ page, isMobile }, info) => {
  test.skip(isMobile, 'one browser changes the schedule at a time')
  test.skip((await (await page.request.get('/api/admin/argus/status')).json()).configured === false, 'needs Argus')
  await page.goto('/admin/indexing')
  const card = page.getByRole('region', { name: 'Schedule' })
  await expect(card.getByRole('switch', { name: 'Reindex on a schedule' })).toBeChecked()
  try {
    await card.getByRole('combobox', { name: 'Repeat', exact: true }).click()
    await page.getByRole('option', { name: 'Every day' }).click()
    await card.getByLabel('At', { exact: true }).fill('02:30')
    await card.getByRole('combobox', { name: 'Time zone' }).fill('Europe/Berlin')
    await expect(card.getByText('Every day at 02:30.')).toBeVisible()
    await card.getByRole('button', { name: 'Save' }).click()
    await expect(card).toContainText('(Europe/Berlin)')
    await expectAccessible(page, info, 'indexing-schedule')
  } finally {
    expect((await page.request.put('/api/admin/argus/schedule', { data: { schedule: '*/15 * * * *', timeZone: 'UTC' }, headers: { 'X-Requested-With': 'e2e' } })).status()).toBe(204)
  }
})
