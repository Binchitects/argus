import { expect, test, type Page, type Route } from '@playwright/test'

/**
 * Nothing in Code Arena scrolls sideways but the editor (and the terminal): a long command, a path with no spaces, a
 * wide code block, a long tool result, a job's long output and a sub-agent's long title wrap or are cut with "…",
 * in the chat, the jobs, the tasks list and a task opened, at a wide window and a narrow one.
 */
const long = 'packages/' + 'very-long-directory-name/'.repeat(14) + 'file.ts'
const command = `docker run --rm -v "$PWD:/work" -w /work node:22 sh -c "npm ci && npm run build -- --filter=${'@shop/package-'.repeat(12)}web && npm test"`

async function backend(page: Page) {
  const state = {
    name: 'Code Arena', version: '8.0.0', license: 'AGPL-3.0-only', source: 'https://github.com/Binchitects/argus', manual: 'https://llm.test/help/code-arena',
    folder: '/home/ada/shop', project: 'shop', branch: 'main', model: 'model-a', context: 262144, contextUsed: 4200, compactAt: 80, compactTarget: 25,
    servers: [], queued: [], thinking: null, mode: 'ask', modes: [{ name: 'ask', description: 'edits and commands ask first' }],
    session: 's1', busy: false, arenaTools: false, tools: { local: 10, servers: [] }, tasksRunning: 1,
    jobs: [1, 2, 3, 4].map((id) => ({ id, command, running: id < 3, status: id < 3 ? 'running for 2m' : 'exit code 0 after 3m', background: id === 1,
      output: Array.from({ length: 400 }, (_, i) => `line ${i} ${'x'.repeat(i % 7 === 0 ? 600 : 40)}`).join('\n') })),
  }
  const config = {
    model: 'model-a',
    models: [{ name: 'model-a', context: 262144, maxOutput: 32768, vision: false, tools: true, thinking: true, loaded: true, prices: { input: null, cachedInput: null, output: null } }],
    auto: null, presets: [], defaultThinking: null, codeIntel: false, tools: [], gitlabUrl: null, maxUploadBytes: 0, imageTypes: [],
  }
  const blank = { quote: null, reasoning: null, toolName: null, toolCallId: null, toolCalls: null, attachments: [], status: 'complete', error: null, model: null,
    promptTokens: null, cachedTokens: null, completionTokens: null, thinkingMs: null, durationMs: null, createdAt: new Date().toISOString(), noAccess: false,
    summary: null, context: null, files: null, details: null }
  const messages = [
    { ...blank, id: 'm0', parentId: null, role: 'user', content: `Build ${long} and run ${command}` },
    { ...blank, id: 'm1', parentId: 'm0', role: 'assistant', content: '', model: 'model-a', reasoning: `Thinking about ${long} `.repeat(20),
      toolCalls: [{ id: 'c1', function: { name: 'run_command', arguments: JSON.stringify({ command }) } }] },
    { ...blank, id: 'm2', parentId: 'm1', role: 'tool', toolCallId: 'c1', toolName: 'run_command', content: `exit code 1\n${'error TS2345: '.repeat(3)}${long}:1:1 ${'y'.repeat(900)}` },
    { ...blank, id: 'm4', parentId: 'm2', role: 'assistant', content: '', model: 'model-a',
      toolCalls: [{ id: 'c2', function: { name: 'edit_file', arguments: JSON.stringify({ path: long, old_string: 'a'.repeat(300), new_string: 'b'.repeat(300) }) } }] },
    { ...blank, id: 'm5', parentId: 'm4', role: 'tool', toolCallId: 'c2', toolName: 'edit_file', content: `Edited ${long}.` },
    { ...blank, id: 'm3', parentId: 'm5', role: 'assistant', model: 'model-a',
      content: `It failed in \`${long}\`:\n\n\`\`\`ts\nexport const ${'veryLongName'.repeat(30)} = await import('${long}')\n\`\`\`\n\n| path | why |\n|---|---|\n| ${long} | ${'z'.repeat(300)} |\n\nRun ${command} again.` },
  ]
  const task = {
    id: 't1', kind: 'subagent', title: `find every caller of ${'someVeryLongFunctionName'.repeat(6)}`, status: 'running', call: 'c9', as: null, model: 'model-a', writes: false,
    created: new Date().toISOString(), started: new Date().toISOString(), ended: null, ms: 4000, tokens: { prompt: 9000, cached: 0, completion: 400 }, calls: 2,
    doing: `grep -rn ${'someVeryLongFunctionName'.repeat(6)} ${long}`, error: null, kept: false,
  }
  const detail = { ...task, prompt: `Find ${long} ${'w'.repeat(500)}`, tools: ['grep', 'read_file'], report: null,
    work: { title: task.title, instructions: '', reasoning: `Reading ${long}`, text: '', status: 'running', error: null, ms: 4000,
      steps: [{ id: 's1', name: 'grep', arguments: JSON.stringify({ pattern: 'q'.repeat(400), path: long }), result: `${long}:1: ${'r'.repeat(800)}` }] } }
  const answers: Record<string, unknown> = {
    '/api/state': state,
    '/api/chat/config': config,
    '/api/session': { id: 's1', messages, busy: false,
      diffs: { m5: { path: long, added: 3, removed: 3, more: 0, created: false,
        lines: Array.from({ length: 6 }, (_, i) => (i % 2 === 0 ? ['-', i + 1, 0, `const old${i} = '${'o'.repeat(700)}'`] : ['+', 0, i, `const new${i} = '${'n'.repeat(700)}'`])) } }, turn: 0, usage: { prompt: 0, cached: 0, completion: 0 } },
    '/api/sessions': [{ id: 's1', title: `A session about ${long}`, updatedAt: new Date().toISOString(), messages: 4 }],
    '/api/preferences': { layout: { chat: true } },
    '/api/history': [],
    '/api/agent-tasks': { tasks: [task], running: 1, mostAtOnce: 4 },
    '/api/agent-tasks/t1': detail,
  }
  await page.route('**/api/**', (route: Route) => {
    const path = new URL(route.request().url()).pathname
    if (route.request().method() === 'POST' && path === '/api/preferences') return route.fulfill({ json: {} })
    const known = answers[path]
    return known !== undefined ? route.fulfill({ json: known }) : route.fulfill({ status: 404, json: { error: 'not_found', message: 'Not here.' } })
  })
}

/** Every box that scrolls sideways (or the page itself), outside the editor and the terminal. */
const sideways = (page: Page) =>
  page.evaluate(() => {
    const out: string[] = []
    const root = document.documentElement
    if (root.scrollWidth > root.clientWidth + 1) out.push(`the page (${root.scrollWidth} > ${root.clientWidth})`)
    for (const el of document.querySelectorAll<HTMLElement>('body *')) {
      if (el.closest('.monaco-editor, .xterm, [data-editor], [aria-label="Editor"], [aria-label^="Editor group"]')) continue
      const x = getComputedStyle(el).overflowX
      if ((x === 'auto' || x === 'scroll') && el.scrollWidth > el.clientWidth + 1) {
        const name = el.getAttribute('aria-label') ?? el.closest('[aria-label]')?.getAttribute('aria-label') ?? ''
        out.push(`${el.tagName.toLowerCase()}.${[...el.classList].slice(0, 4).join('.')} in "${name}" (${el.scrollWidth} > ${el.clientWidth})`)
      }
    }
    return out
  })

for (const width of [1400, 900]) {
  test(`nothing but the editor scrolls sideways at ${width} px: the chat, its jobs, the tasks and a task`, async ({ page }) => {
    test.setTimeout(120_000)
    await page.setViewportSize({ width, height: 900 })
    await backend(page)
    await page.goto('/code-arena.html')
    const chat = page.getByRole('region', { name: 'Chat' })
    await expect(chat.getByText(/Run docker run/)).toBeVisible()
    // Every fold of the thread open: the tool's call and result, the thinking.
    for (const fold of await chat.locator('[data-thread] button[aria-expanded="false"]').all()) await fold.click({ timeout: 2000 }).catch(() => {})
    await page.keyboard.press('Escape')
    expect(await sideways(page)).toEqual([])
    // Four long-running commands printing 400 long lines each: a quarter of the window at most, one output open.
    const jobs = page.getByRole('region', { name: 'Commands with no time limit' })
    expect((await jobs.boundingBox())!.height).toBeLessThanOrEqual(900 * 0.25 + 32)
    await expect(jobs.getByLabel(/^Output of job/)).toHaveCount(1)
    await expect(jobs.getByLabel('Output of job 2')).toBeVisible()

    await chat.getByRole('button', { name: /^Tasks/ }).click()
    const tasks = page.getByRole('complementary', { name: 'Tasks' })
    await expect(tasks.getByText(/find every caller/)).toBeVisible()
    expect(await sideways(page)).toEqual([])
    await tasks.getByRole('button', { name: /^find every caller.*: Working$/ }).click()
    await expect(tasks.getByRole('region', { name: 'What it was asked' })).toBeVisible()
    for (const fold of await tasks.locator('[aria-busy] button[aria-expanded="false"]').all()) await fold.click({ timeout: 2000 }).catch(() => {})
    expect(await sideways(page)).toEqual([])
  })
}

test('nothing but the editor scrolls sideways in any view of the side bar or the panel, at 900 px', async ({ page }) => {
  test.setTimeout(180_000)
  await page.setViewportSize({ width: 900, height: 800 })
  await backend(page)
  await page.goto('/code-arena.html')
  await expect(page.getByRole('region', { name: 'Chat' }).getByText(/Run docker run/)).toBeVisible()
  const seen: string[] = []
  const bar = page.getByRole('navigation', { name: 'Activity bar' }).first()
  for (const view of await bar.getByRole('button').all()) {
    const name = (await view.getAttribute('aria-label')) ?? ''
    if ((await view.getAttribute('aria-pressed')) !== 'true') await view.click({ timeout: 3000 }).catch(() => {})
    await page.keyboard.press('Escape')
    for (const found of await sideways(page)) seen.push(`${name}: ${found}`)
  }
  // The panel, each of its views.
  const tabs = page.getByRole('tablist', { name: 'Panel' })
  if (!(await tabs.isVisible())) await page.keyboard.press('Control+j')
  await expect(tabs).toBeVisible()
  if (await tabs.isVisible()) {
    for (const tab of await tabs.getByRole('tab').all()) {
      const name = (await tab.textContent()) ?? ''
      await tab.click({ timeout: 3000 }).catch(() => {})
      for (const found of await sideways(page)) seen.push(`panel ${name}: ${found}`)
    }
  }
  expect([...new Set(seen)]).toEqual([])
  // The panel's tabs that do not fit are in its "…" menu; one chosen there is the open tab, shown in the strip.
  const more = page.getByRole('button', { name: 'More panel views' })
  await expect(more).toBeVisible()
  await more.click()
  const item = page.getByRole('menuitem').first()
  const picked = ((await item.textContent()) ?? '').trim()
  await item.click()
  await expect(page.getByRole('tablist', { name: 'Panel' }).getByRole('tab', { name: picked, exact: false })).toHaveAttribute('aria-selected', 'true')
  expect(await sideways(page)).toEqual([])
})
