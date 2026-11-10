import { expect, test, type Page, type Route } from '@playwright/test'

/** code-arena web while an answer is written, with two messages sent meanwhile; PATCH and DELETE of one change the line. */
async function backend(page: Page) {
  let queued = [
    { id: 'q1', text: 'Also run the unit tests', quote: null, attachments: null },
    { id: 'q2', text: 'and lint the changed files', quote: null, attachments: null },
  ]
  const state = () => ({
    name: 'Code Arena', version: '7.8.0', license: 'AGPL-3.0-only', source: 'https://github.com/Binchitects/argus', manual: 'https://llm.test/help/code-arena',
    folder: '/home/ada/shop', project: 'shop', branch: 'main', model: 'model-a', context: 262144, contextUsed: 4200, compactAt: 80, compactTarget: 25,
    servers: [], jobs: [], queued, thinking: null, mode: 'ask', modes: [{ name: 'ask', description: 'edits and commands ask first' }],
    session: 's1', busy: false, arenaTools: false, tools: { local: 10, servers: [] },
  })
  const config = {
    model: 'model-a',
    models: [{ name: 'model-a', context: 262144, maxOutput: 32768, vision: false, tools: true, thinking: true, loaded: true, prices: { input: null, cachedInput: null, output: null } }],
    auto: null, presets: [], defaultThinking: null, codeIntel: false, tools: [], gitlabUrl: null, maxUploadBytes: 0, imageTypes: [],
  }
  const blank = { quote: null, reasoning: null, toolName: null, toolCallId: null, toolCalls: null, attachments: [], status: 'complete', error: null, model: null,
    promptTokens: null, cachedTokens: null, completionTokens: null, thinkingMs: null, durationMs: null, createdAt: new Date().toISOString(), noAccess: false,
    summary: null, context: null, files: null, details: null }
  const session = {
    id: 's1', diffs: {}, busy: false, turn: 0, usage: { prompt: 0, cached: 0, completion: 0 },
    messages: [
      { ...blank, id: 'm0', parentId: null, role: 'user', content: 'Fix the cart total' },
      { ...blank, id: 'm1', parentId: 'm0', role: 'assistant', content: 'Reading the cart first.', model: 'model-a' },
    ],
  }
  const changed: string[] = []
  const answers: Record<string, () => unknown> = {
    '/api/state': state,
    '/api/chat/config': () => config,
    '/api/session': () => session,
    '/api/sessions': () => [{ id: 's1', title: 'Cart total', updatedAt: new Date().toISOString(), messages: 2 }],
    '/api/preferences': () => ({ layout: { chat: true } }),
    '/api/history': () => [],
  }
  await page.route('**/api/**', (route: Route) => {
    const path = new URL(route.request().url()).pathname
    const method = route.request().method()
    if (path.startsWith('/api/queue/') && (method === 'PATCH' || method === 'DELETE')) {
      const id = path.slice('/api/queue/'.length)
      changed.push(`${method} ${id}`)
      queued = method === 'DELETE' ? queued.filter((q) => q.id !== id) : queued.map((q) => (q.id === id ? { ...q, text: (route.request().postDataJSON() as { text: string }).text } : q))
      return route.fulfill({ status: 204 })
    }
    const known = answers[path]
    return known ? route.fulfill({ json: known() }) : route.fulfill({ status: 404, json: { error: 'not_found', message: 'Not here.' } })
  })
  return changed
}

for (const scheme of ['light', 'dark'] as const) {
  test(`messages sent while it works wait pending at the end of the thread, to edit or cancel (${scheme})`, async ({ page }) => {
    await page.emulateMedia({ colorScheme: scheme })
    const changed = await backend(page)
    await page.goto('/code-arena.html')
    const chat = page.getByRole('region', { name: 'Chat' })
    const pending = chat.getByRole('region', { name: 'Pending messages' })
    await expect(pending.getByText('Also run the unit tests')).toBeVisible()
    // In the thread, after its last answer, and above the box.
    const answer = await chat.getByText('Reading the cart first.').boundingBox()
    const box = await pending.boundingBox()
    const composer = await chat.getByRole('textbox', { name: 'Message' }).boundingBox()
    expect(box!.y).toBeGreaterThan(answer!.y)
    expect(box!.y + box!.height).toBeLessThanOrEqual(composer!.y)
    // Inside the chat's column, however narrow it is.
    const column = await chat.boundingBox()
    expect(box!.x).toBeGreaterThanOrEqual(column!.x)
    expect(box!.x + box!.width).toBeLessThanOrEqual(column!.x + column!.width)

    await pending.getByRole('button', { name: 'Edit queued message: Also run the unit tests' }).click()
    const editing = pending.getByRole('textbox', { name: 'Edit the queued message' })
    await expect(editing).toBeFocused()
    await editing.fill('Also run the integration tests')
    await editing.press('Enter')
    await expect(pending.getByText('Also run the integration tests')).toBeVisible()
    await pending.getByRole('button', { name: 'Cancel queued message: and lint the changed files' }).click()
    await expect(pending.getByText('and lint the changed files')).toHaveCount(0)
    expect(changed).toEqual(['PATCH q1', 'DELETE q2'])
  })
}
