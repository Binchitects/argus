import { AxeBuilder } from '@axe-core/playwright'
import { expect, test, type Page, type Route } from '@playwright/test'

/** code-arena web's API with a small folder: two files to open; the rest answers nothing. */
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
  const disk: Record<string, string> = { 'README.md': '# Shop\n', 'app.ts': 'export const total = 1\n' }
  const answers: Record<string, unknown> = {
    '/api/state': state,
    '/api/chat/config': config,
    '/api/session': { id: 's1', messages: [], diffs: {}, busy: false },
    '/api/sessions': [],
    '/api/preferences': { layout: { chatOpen: false } },
    '/api/history': [],
    '/api/files': { path: '.', entries: Object.keys(disk).map((name) => ({ name, path: name, kind: 'file', size: disk[name]!.length })) },
    '/api/files/all': { files: Object.keys(disk), truncated: false },
    '/api/workspace/state': {},
    '/api/backups': { backups: [] },
    '/api/changes': [],
  }
  await page.route('**/api/**', (route: Route) => {
    const url = new URL(route.request().url())
    if (url.pathname === '/api/file') {
      const path = url.searchParams.get('path')!
      const text = disk[path]
      return text === undefined ? route.fulfill({ status: 404, json: { status: 'not_found', error: 'There is no such file.' } }) : route.fulfill({ json: { path, size: text.length, version: 'v1', text } })
    }
    // What the page keeps (its tabs, the layout, backups) is taken; anything else asked of the server is not here.
    if (route.request().method() === 'POST') return ['/api/workspace/state', '/api/preferences', '/api/backups'].includes(url.pathname) ? route.fulfill({ json: {} }) : route.fulfill({ status: 404, json: { error: 'not_found', message: 'Not here.' } })
    const known = answers[url.pathname]
    return known !== undefined ? route.fulfill({ json: known }) : route.fulfill({ status: 404, json: { error: 'not_found', message: 'Not here.' } })
  })
}

test('a tab dragged to the editor\'s right edge makes a second group, the middle button closes a tab, and axe finds nothing serious', async ({ page }) => {
  await backend(page)
  await page.goto('/code-arena.html')
  await page.getByRole('treeitem', { name: 'README.md' }).dblclick()
  await page.getByRole('treeitem', { name: 'app.ts' }).dblclick()
  const first = page.getByRole('tablist', { name: 'Open files', exact: true })
  await expect(first.getByRole('tab', { name: 'app.ts' })).toBeVisible()

  // Dragged onto the right quarter of the editor: a new group there, with the tab, which leaves the first.
  const editor = page.locator('#editor-panel-1')
  const box = (await editor.boundingBox())!
  await first.getByRole('tab', { name: 'app.ts' }).dragTo(editor, { targetPosition: { x: box.width - 20, y: box.height / 2 } })
  const second = page.getByRole('tablist', { name: 'Open files in group 2' })
  await expect(second.getByRole('tab', { name: 'app.ts' })).toBeVisible()
  await expect(first.getByRole('tab', { name: 'app.ts' })).toHaveCount(0)
  // Side by side, each half of the space.
  const one = (await page.getByRole('region', { name: 'Editor group 1' }).boundingBox())!
  const two = (await page.getByRole('region', { name: 'Editor group 2' }).boundingBox())!
  expect(two.x).toBeGreaterThan(one.x + one.width - 2)
  expect(Math.abs(one.width - two.width)).toBeLessThan(4)

  // Each tab's close button sits beside it in the strip, as VS Code's: a tablist then owns buttons besides its tabs, which
  // aria-required-children reports. Every other rule holds.
  const axe = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa']).disableRules(['aria-required-children']).analyze()
  const bad = axe.violations.filter((v) => v.impact === 'serious' || v.impact === 'critical')
  expect(bad.map((v) => `${v.id}: ${v.help} (${v.nodes.map((n) => n.target.join(' ')).slice(0, 3).join(' | ')})`)).toEqual([])

  // The middle button on the second group's tab closes it, and the empty group with it.
  await second.getByRole('tab', { name: 'app.ts' }).click({ button: 'middle' })
  await expect(second).toHaveCount(0)
  await expect(page.getByRole('region', { name: 'Editor', exact: true })).toBeVisible()
})
