import { expect, test, type Locator, type Page, type Route } from '@playwright/test'

/** code-arena web's API as the page reads it at the start, with the session given; the rest answers nothing. */
async function backend(page: Page, session: object) {
  const state = {
    name: 'Code Arena', version: '7.8.0', license: 'AGPL-3.0-only', source: 'https://github.com/Binchitects/argus', manual: 'https://llm.test/help/code-arena',
    folder: '/home/ada/shop', project: 'shop', branch: 'main', model: 'model-a', context: 262144, contextUsed: 4200, compactAt: 80, compactTarget: 25,
    servers: [], jobs: [], queued: [], thinking: null, mode: 'ask', modes: [{ name: 'ask', description: 'edits and commands ask first' }],
    session: 's1', busy: false, arenaTools: false, tools: { local: 10, servers: [] },
  }
  const config = {
    model: 'model-a',
    models: [{ name: 'model-a', context: 262144, maxOutput: 32768, vision: false, tools: true, thinking: true, loaded: true, prices: { input: null, cachedInput: null, output: null } }],
    auto: null, presets: [], defaultThinking: null, codeIntel: false, tools: [], gitlabUrl: null, maxUploadBytes: 0, imageTypes: [],
  }
  const answers: Record<string, unknown> = {
    '/api/state': state,
    '/api/chat/config': config,
    '/api/session': session,
    '/api/sessions': [{ id: 's1', title: 'A long session', updatedAt: new Date().toISOString(), messages: 1 }],
    '/api/preferences': { layout: { chat: true } },
    '/api/history': [],
  }
  await page.route('**/api/**', (route: Route) => {
    const path = new URL(route.request().url()).pathname
    const known = answers[path]
    return known !== undefined ? route.fulfill({ json: known }) : route.fulfill({ status: 404, json: { error: 'not_found', message: 'Not here.' } })
  })
}

/** A session of `turns` turns, each an edit of `lines` lines with its diff, and an answer. */
function longSession(turns: number, lines: number) {
  const messages: object[] = []
  const diffs: Record<string, object> = {}
  const blank = { quote: null, reasoning: null, toolName: null, toolCallId: null, toolCalls: null, attachments: [], status: 'complete', error: null, model: null,
    promptTokens: null, cachedTokens: null, completionTokens: null, thinkingMs: null, durationMs: null, createdAt: new Date().toISOString(), noAccess: false,
    summary: null, context: null, files: null, details: null }
  const add = (m: object) => {
    const i = messages.length
    messages.push({ ...blank, id: `m${i}`, parentId: i === 0 ? null : `m${i - 1}`, ...m })
    return `m${i}`
  }
  for (let t = 0; t < turns; t++) {
    add({ role: 'user', content: `Change file ${t}` })
    const call = `c${t}`
    add({ role: 'assistant', content: '', model: 'model-a', toolCalls: [{ id: call, function: { name: 'edit_file', arguments: JSON.stringify({ path: `src/f${t}.ts`, old_string: 'a', new_string: 'b' }) } }] })
    const result = add({ role: 'tool', content: `Edited src/f${t}.ts.`, toolCallId: call, toolName: 'edit_file' })
    diffs[result] = {
      path: `src/f${t}.ts`, added: lines, removed: lines, more: 0, created: false,
      lines: Array.from({ length: lines * 2 }, (_, i) => (i % 2 === 0 ? ['-', i / 2 + 1, 0, `old line ${i}`] : ['+', 0, (i - 1) / 2 + 1, `new line ${i}`])),
    }
    add({ role: 'assistant', content: `Changed src/f${t}.ts.`, model: 'model-a' })
  }
  return { id: 's1', messages, diffs, busy: false, turn: 0, usage: { prompt: 0, cached: 0, completion: 0 } }
}

/** The thread's height to scroll, and the height of its turns. */
const heights = (chat: Locator) =>
  chat.evaluate((section) => {
    const thread = section.querySelector<HTMLElement>('[data-thread]')!
    return { scroll: thread.scrollHeight, turns: (thread.firstElementChild as HTMLElement).offsetHeight }
  })

test('the thread is as tall as its turns after many turns, their diffs open as a running turn shows them', async ({ page }) => {
  await backend(page, longSession(12, 150))
  await page.goto('/code-arena.html')
  const chat = page.getByRole('region', { name: 'Chat' })
  await expect(chat.getByText('Changed src/f11.ts.')).toBeVisible()
  expect((await heights(chat)).scroll).toBeLessThanOrEqual((await heights(chat)).turns + 1)

  // Each edit's lines shown, as while its turn ran: 300 lines in a box of their own that scrolls.
  const folded = chat.getByRole('button', { name: /^Changed lines of / })
  for (let i = 0; i < (await folded.count()); i++) await folded.nth(i).click()
  await expect(chat.getByRole('figure', { name: 'Changes to src/f11.ts' }).getByText('new line 299')).toBeAttached()
  const { scroll, turns } = await heights(chat)
  // What scrolls is the turns and nothing under them: the lines a diff's box hides are not laid out past its end.
  expect(scroll).toBeLessThanOrEqual(turns + 1)
})
