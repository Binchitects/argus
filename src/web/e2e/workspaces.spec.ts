import { expect, test } from '@playwright/test'
import { watchConsole } from './helpers.ts'

/**
 * Code Studio's workspaces in a browser, against a stack with a real workspaces service and a code host
 * (ci/workspaces-stack.sh; E2E_WORKSPACES=1): a workspace made from a project and started (cloned with the person's
 * token), its IDE open on the clone, a dev server run in its terminal, listed in the Ports panel with its process, and
 * opened at its forwarded address in a tab of its own; then put to sleep. Any other stack has no workspaces: skipped.
 */
test('a workspace starts, its IDE opens the clone, a dev server in its terminal shows in Ports and opens at its forwarded address', async ({ page, context, baseURL }) => {
  test.skip(process.env.E2E_WORKSPACES !== '1', 'needs a stack with a workspaces service and a code host (ci/workspaces-stack.sh)')
  test.setTimeout(240_000)
  const errors = watchConsole(page)
  const headers = { 'X-Requested-With': 'e2e' }
  // The person's GitLab token, as Code Studio's token card saves it: the code host knows this one.
  expect((await page.request.put('/api/fixes/token', { data: { token: 'glpat-e2e-token' }, headers })).ok()).toBe(true)
  let id = ''
  try {
    await page.goto('/code')
    const form = page.getByRole('form', { name: 'New workspace' })
    await form.getByLabel('Project').fill('group/app')
    await form.getByRole('button', { name: 'Open it' }).click()
    await page.waitForURL(/\/code\/w\/[0-9a-f-]{36}$/)
    id = page.url().split('/').at(-1)!
    // Started (the first time, cloned), then the IDE on the clone: its README in the explorer.
    await expect(page.getByRole('treeitem', { name: 'README.md' })).toBeVisible({ timeout: 120_000 })
    await expect(page.getByRole('button', { name: 'Put to sleep' })).toBeEnabled()

    // The terminals (the status bar's button, as Ctrl+`), a new one (as VS Code's +), and a dev server in it.
    await page.getByRole('button', { name: 'Show or hide the terminal' }).click()
    const terminal = page.getByRole('region', { name: 'Terminal' })
    await terminal.getByRole('button', { name: 'New terminal' }).click({ timeout: 15_000 })
    // The one shown (its tab's panel; the others are hidden).
    const screen = terminal.getByRole('tabpanel').locator('[data-testid^="xterm-"]')
    await expect(screen).toBeVisible({ timeout: 30_000 })
    await screen.click()
    await page.keyboard.type('python3 -m http.server 5173 --bind 127.0.0.1')
    await page.keyboard.press('Enter')
    await expect(screen).toContainText('Serving HTTP on 127.0.0.1 port 5173', { timeout: 30_000 })

    // The Ports panel lists it, found by itself, with its process; its forwarded address is its own name.
    await page.getByRole('tablist', { name: 'Panel' }).getByRole('tab', { name: 'Ports' }).click()
    const ports = page.getByRole('region', { name: 'Ports' })
    const row = ports.getByRole('row').filter({ hasText: '5173' })
    await expect(row).toBeVisible({ timeout: 30_000 })
    await expect(row).toContainText('http.server')
    const link = row.getByRole('link')
    const host = new URL(baseURL!).host
    await expect(link).toHaveAttribute('href', new RegExp(`^https://ws-${id.replaceAll('-', '')}-5173\\.${host.replace(/[.]/g, '\\.')}/?$`))

    // Opened in a tab of its own: the dev server's page (the clone's files), reached as its owner.
    const [tab] = await Promise.all([context.waitForEvent('page'), link.click()])
    await tab.waitForLoadState()
    await expect(tab.locator('body')).toContainText('README.md', { timeout: 30_000 })
    await tab.close()

    // Nothing failed on the way but the IDE's look for the folder's own VS Code settings, which this clone has none of.
    expect(errors.filter((e) => !(e.includes('status of 404') && e.includes('.vscode%2Fsettings.json')))).toEqual([])

    // Put to sleep: back on Code Studio, where it says so (the IDE's watches still open as it sleeps end with 409).
    await page.getByRole('button', { name: 'Put to sleep' }).click()
    await page.waitForURL(/\/code$/)
    const listed = page.getByRole('list', { name: 'Your workspaces' }).getByRole('listitem').filter({ hasText: 'group/app' })
    await expect(listed.first()).toContainText('Asleep')
  } finally {
    if (id) await page.request.delete(`/api/studio/workspaces/${id}`, { headers })
    await page.request.delete('/api/fixes/token', { headers })
  }
})
