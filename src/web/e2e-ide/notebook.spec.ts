import { AxeBuilder } from '@axe-core/playwright'
import { expect, test, type Page, type Route } from '@playwright/test'

const notebook = JSON.stringify(
  {
    cells: [
      { cell_type: 'markdown', id: 'm1', metadata: {}, source: ['# Sales\n', 'By month.'] },
      { cell_type: 'code', execution_count: null, id: 'c1', metadata: {}, outputs: [], source: ["print('hello')\n", 'df'] },
    ],
    metadata: { kernelspec: { display_name: 'Python 3', language: 'python', name: 'python3' }, language_info: { name: 'python' } },
    nbformat: 4,
    nbformat_minor: 5,
  },
  null,
  1,
) + '\n'

// A 1x1 PNG.
const png = 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=='

/** code-arena web's API with a notebook and a kernel that answers each cell: what it printed first, then a picture and a table. */
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
  const disk: Record<string, string> = { 'analysis.ipynb': notebook }
  const done = { saved: null as string | null, executed: [] as string[] }
  let count = 0
  const answers: Record<string, unknown> = {
    '/api/state': state,
    '/api/chat/config': config,
    '/api/session': { id: 's1', messages: [], diffs: {}, busy: false },
    '/api/sessions': [],
    '/api/preferences': { layout: { chatOpen: false } },
    '/api/history': [],
    '/api/files': { path: '.', entries: [{ name: 'analysis.ipynb', path: 'analysis.ipynb', kind: 'file', size: notebook.length }] },
    '/api/files/all': { files: ['analysis.ipynb'], truncated: false },
    '/api/workspace/state': {},
    '/api/backups': { backups: [] },
    '/api/changes': [],
    '/api/snippets': { files: [], problems: [] },
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
    if (url.pathname === '/api/notebook/kernel') return route.fulfill({ json: { python: '/home/ada/shop/.venv/bin/python', running: count > 0, busy: false, count, trusted: true } })
    if (url.pathname === '/api/notebook/execute') {
      done.executed.push((route.request().postDataJSON() as { code: string }).code)
      count++
      return route.fulfill({ json: { run: `r${count}` } })
    }
    if (url.pathname === '/api/notebook/run') {
      const since = Number(url.searchParams.get('since'))
      const printed = { output_type: 'stream', name: 'stdout', text: 'hello\n' }
      if (since === 0) return route.fulfill({ json: { version: 1, outputs: [printed], done: false, ok: true, count } })
      return route.fulfill({
        json: {
          version: 3,
          done: true,
          ok: true,
          count,
          outputs: [
            printed,
            { output_type: 'display_data', data: { 'image/png': png, 'text/plain': '<Figure>' }, metadata: {} },
            { output_type: 'execute_result', execution_count: count, data: { 'text/html': '<table><tr><th>month</th><td>12</td></tr></table>', 'text/plain': 'df' }, metadata: {} },
          ],
        },
      })
    }
    if (post) return ['/api/workspace/state', '/api/preferences', '/api/backups'].includes(url.pathname) ? route.fulfill({ json: {} }) : route.fulfill({ status: 404, json: { error: 'not_found', message: 'Not here.' } })
    const known = answers[url.pathname]
    return known !== undefined ? route.fulfill({ json: known }) : route.fulfill({ status: 404, json: { error: 'not_found', message: 'Not here.' } })
  })
  return done
}

test('a notebook opens as cells, a cell runs in its kernel with its outputs, and saving writes them into the .ipynb', async ({ page }) => {
  const done = await backend(page)
  await page.goto('/code-arena.html')
  await page.getByRole('treeitem', { name: 'analysis.ipynb' }).dblclick()
  const nb = page.getByTestId('notebook-editor')
  await expect(nb.getByRole('heading', { name: 'Sales' })).toBeVisible()
  await expect(nb.getByTestId('notebook-cell')).toHaveCount(2)
  await expect(nb.getByText('.venv/bin/python', { exact: false })).toBeVisible()

  // axe before the HTML output's frame is there (a frame that runs no script cannot be looked into).
  const axe = await new AxeBuilder({ page }).include('[data-testid="notebook-editor"]').withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa']).analyze()
  expect(axe.violations.filter((v) => v.impact === 'serious' || v.impact === 'critical').map((v) => `${v.id}: ${v.help} (${v.nodes.map((n) => n.target.join(' ')).slice(0, 3).join(' | ')})`)).toEqual([])

  // Run: what it printed, its picture and its table (in a frame that runs no script), its count.
  await nb.getByRole('button', { name: 'Run Code cell 2' }).click()
  const outputs = nb.getByTestId('cell-outputs')
  await expect(outputs.getByText('hello')).toBeVisible()
  await expect(outputs.getByRole('img', { name: 'Cell output' })).toBeVisible()
  const table = outputs.frameLocator('iframe[title="HTML output"]')
  await expect(table.getByText('month')).toBeVisible()
  await expect(outputs.locator('iframe')).toHaveAttribute('sandbox', 'allow-same-origin')
  await expect(nb.getByLabel('run 1')).toBeVisible()
  expect(done.executed).toEqual(["print('hello')\ndf"])
  await expect(page.getByRole('tab', { name: /analysis\.ipynb/ })).toContainText(', not saved')

  // A Markdown cell added below, written, shown as it reads with Ctrl+Enter (Shift+Enter would add a cell after it).
  await nb.getByRole('button', { name: 'Markdown' }).first().click()
  await expect(nb.getByTestId('notebook-cell')).toHaveCount(3)
  await page.keyboard.type('**Done** here')
  await page.keyboard.press('Control+Enter')
  await expect(nb.getByText('Done', { exact: true })).toBeVisible()
  await expect(nb.getByTestId('notebook-cell')).toHaveCount(3)

  await page.keyboard.press('Control+s')
  await expect.poll(() => done.saved).not.toBeNull()
  const saved = JSON.parse(done.saved!)
  expect(saved.cells).toHaveLength(3)
  expect(saved.cells[1].execution_count).toBe(1)
  expect(saved.cells[1].outputs.map((o: { output_type: string }) => o.output_type)).toEqual(['stream', 'display_data', 'execute_result'])
  expect(saved.cells[1].outputs[0].text).toEqual(['hello\n'])
  expect(saved.cells[2]).toMatchObject({ cell_type: 'markdown', source: ['**Done** here'] })
  // As Jupyter writes it: one space of indentation, keys sorted.
  expect(done.saved!.startsWith('{\n "cells": [')).toBe(true)
  await expect(page.getByRole('tab', { name: /analysis\.ipynb/ })).not.toContainText(', not saved')
})
