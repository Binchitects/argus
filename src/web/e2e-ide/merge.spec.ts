import { AxeBuilder } from '@axe-core/playwright'
import { expect, test, type Page, type Route } from '@playwright/test'

const conflicted = [
  'export const items = []',
  '<<<<<<< HEAD',
  'export const tax = 0.2',
  '=======',
  'export const tax = 0.25',
  '>>>>>>> feature',
  'export const shipping = 5',
  '<<<<<<< HEAD',
  'export const name = "shop"',
  '=======',
  'export const name = "store"',
  '>>>>>>> feature',
  '',
].join('\n')

/** code-arena web's API with one file in conflict (a merge of feature into main); what is saved and staged is kept. */
async function backend(page: Page) {
  const state = {
    name: 'Code Arena', version: '7.8.0', license: 'AGPL-3.0-only', source: 'https://github.com/Binchitects/argus', manual: 'https://llm.test/help/code-arena',
    folder: '/home/ada/shop', project: 'shop', branch: 'main', model: 'model-a', context: 262144, contextUsed: 4200, compactAt: 80, compactTarget: 25,
    servers: [], jobs: [], queued: [], thinking: null, mode: 'ask', modes: [{ name: 'ask', description: 'edits and commands ask first' }],
    session: 's1', busy: false, arenaTools: false, tools: { local: 10, servers: [] },
  }
  const config = {
    model: 'model-a',
    models: [{ name: 'model-a', context: 262144, maxOutput: 32768, vision: false, tools: true, thinking: true, loaded: true, prices: { input: null, cachedInput: null, output: null } }],
    auto: null, presets: [], defaultThinking: null, argus: false, tools: [], gitlabUrl: null, maxUploadBytes: 0, imageTypes: [],
  }
  const disk: Record<string, string> = { 'cart.ts': conflicted }
  const done = { saved: null as string | null, completed: null as unknown }
  const side = (name: string, short: string, subject: string) => ({ name, commit: short.padEnd(40, '0'), short, subject })
  const answers: Record<string, unknown> = {
    '/api/state': state,
    '/api/chat/config': config,
    '/api/session': { id: 's1', messages: [], diffs: {}, busy: false },
    '/api/sessions': [],
    '/api/preferences': { layout: { chatOpen: false } },
    '/api/history': [],
    '/api/files': { path: '.', entries: [{ name: 'cart.ts', path: 'cart.ts', kind: 'file', size: conflicted.length }] },
    '/api/files/all': { files: ['cart.ts'], truncated: false },
    '/api/workspace/state': {},
    '/api/backups': { backups: [] },
    '/api/changes': [],
    '/api/snippets': { files: [], problems: [] },
    '/api/git/merge': { path: 'cart.ts', unmerged: true, stages: [1, 2, 3], kind: 'merge', current: side('main', 'a1b2c3d', 'Raise the tax'), incoming: side('feature', 'e4f5a6b', 'Rename the shop') },
  }
  await page.route('**/api/**', (route: Route) => {
    const url = new URL(route.request().url())
    const post = route.request().method() === 'POST'
    if (url.pathname === '/api/file' && !post) {
      const path = url.searchParams.get('path')!
      const text = disk[path]
      return text === undefined ? route.fulfill({ status: 404, json: { status: 'not_found', error: 'There is no such file.' } }) : route.fulfill({ json: { path, size: text.length, version: 'v1', text } })
    }
    if (url.pathname === '/api/file' && post) {
      const body = route.request().postDataJSON() as { path: string; text: string }
      disk[body.path] = body.text
      done.saved = body.text
      return route.fulfill({ json: { path: body.path, version: 'v2', size: body.text.length } })
    }
    if (url.pathname === '/api/git/merge/complete') {
      done.completed = route.request().postDataJSON()
      return route.fulfill({ json: { repository: true, branch: 'main', files: [] } })
    }
    if (post) return ['/api/workspace/state', '/api/preferences', '/api/backups'].includes(url.pathname) ? route.fulfill({ json: {} }) : route.fulfill({ status: 404, json: { error: 'not_found', message: 'Not here.' } })
    const known = answers[url.pathname]
    return known !== undefined ? route.fulfill({ json: known }) : route.fulfill({ status: 404, json: { error: 'not_found', message: 'Not here.' } })
  })
  return done
}

test('a conflict in the editor has Accept Current, Incoming and Both above it, and one click resolves it', async ({ page }) => {
  await backend(page)
  await page.goto('/code-arena.html')
  await page.getByRole('treeitem', { name: 'cart.ts' }).dblclick()
  const editor = page.getByTestId('code-editor')
  await expect(editor.getByText('Accept Both Changes').first()).toBeVisible()
  await expect(editor.locator('.arena-merge-current-header')).toHaveCount(2)
  await expect(editor.locator('.arena-merge-incoming')).toHaveCount(2)
  await editor.getByText('Accept Incoming Change').first().click()
  // The first conflict is the incoming side now; the second is still there.
  await expect(editor.locator('.view-lines')).toContainText('export const tax = 0.25')
  await expect(editor.locator('.view-lines')).not.toContainText(/tax = 0\.2(?!5)/)
  await expect(editor.getByText('Accept Both Changes')).toHaveCount(1)
})

test('the merge editor takes a side of each conflict, and Complete Merge saves the file and stages it', async ({ page }) => {
  const done = await backend(page)
  await page.goto('/code-arena.html')
  await page.getByRole('treeitem', { name: 'cart.ts' }).dblclick()
  await page.getByTestId('code-editor').getByText('Resolve in Merge Editor').first().click()
  const merge = page.getByTestId('merge-editor')
  await expect(merge.getByRole('status')).toHaveText('2 of 2 conflicts remaining')
  await expect(merge.getByText('feature · e4f5a6b Rename the shop')).toBeVisible()
  await expect(merge.getByText('main · a1b2c3d Raise the tax')).toBeVisible()

  // The incoming tax, the current name.
  await merge.getByTestId('merge-incoming').getByText('☐ Accept Incoming').first().click()
  await expect(merge.getByRole('status')).toHaveText('1 of 2 conflicts remaining')
  await merge.getByTestId('merge-current').getByText('☐ Accept Current').last().click()
  await expect(merge.getByRole('status')).toHaveText('No conflicts remaining')
  await expect(merge.getByTestId('merge-incoming').getByText('☑ Accept Incoming')).toHaveCount(1)
  await expect(page.getByRole('tab', { name: /cart\.ts/ }).last()).toContainText('merge')

  const axe = await new AxeBuilder({ page }).include('[data-testid="merge-editor"]').withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa']).analyze()
  expect(axe.violations.filter((v) => v.impact === 'serious' || v.impact === 'critical').map((v) => `${v.id}: ${v.help} (${v.nodes.map((n) => n.target.join(' ') + ' ' + n.failureSummary).slice(0, 3).join(' | ')})`)).toEqual([])

  await merge.getByRole('button', { name: 'Complete Merge' }).click()
  await expect.poll(() => done.completed).toEqual({ path: 'cart.ts', anyway: false })
  expect(done.saved).toBe(['export const items = []', 'export const tax = 0.25', 'export const shipping = 5', 'export const name = "shop"', ''].join('\n'))
  await expect(merge).toHaveCount(0)
})
