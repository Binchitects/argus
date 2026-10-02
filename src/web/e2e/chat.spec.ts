import { expect, test, type Locator, type Page } from '@playwright/test'
import { expectAccessible, screenshot, watchConsole, withTheme } from './helpers.ts'

// With a real model behind the gateway (the deployed stack): E2E_CHAT=1.
// Without one (CI), only the "no gateway" behaviour is checked.
const live = process.env.E2E_CHAT === '1'

// Every test signs in as the same admin, and fair use runs one person's answers
// one at a time: side by side, they queue behind each other past their timeouts.
// So this file's tests run in order (a failure still leaves the rest to run).
test.describe.configure({ mode: 'default' })

async function ask(page: Page, text: string) {
  await page.getByRole('textbox', { name: 'Message' }).fill(text)
  await page.getByRole('textbox', { name: 'Message' }).press('Enter')
  // On its way once it is on screen. Before that (a new chat is made first)
  // "Send" still shows, and a wait for the answer to end would pass at once.
  await expect(page.getByRole('region', { name: 'You' }).last()).toContainText(text.split('\n')[0]!.slice(0, 40), { timeout: 30_000 })
}

/** Picks a thinking level for the chat (or the new chat) on screen. */
async function thinking(page: Page, label: string) {
  await page.getByRole('combobox', { name: 'Thinking' }).click()
  await page.getByRole('option', { name: label }).click()
}

const done = (page: Page) => expect(page.getByRole('button', { name: 'Send' })).toBeVisible({ timeout: 120_000 })

/** On a phone the chat list is a panel behind "Chats" (it may be open already). */
async function chatList(page: Page, isMobile: boolean) {
  const list = page.getByRole('navigation', { name: 'Chats' })
  if (isMobile && !(await list.isVisible())) await page.getByRole('button', { name: 'Chats', exact: true }).click()
  return list
}

test.describe('chat without a model', () => {
  test.skip(live, 'only where no gateway answers')

  test('sending says the model cannot be reached, and the chat is kept', async ({ page, isMobile }) => {
    // Unique: every project and retry shares one database.
    const text = `hello there ${Date.now()}`
    await page.goto('/chat')
    await ask(page, text)
    await expect(page.getByRole('alert')).toContainText(/not reachable|could not answer/)
    await expect(page).toHaveURL(/\/chat\/[0-9a-f-]{36}$/)
    await expect((await chatList(page, isMobile)).getByRole('link', { name: text })).toBeVisible()
  })
})

// A saved chat with Argus's answers and two images, served by the browser itself:
// how they look needs neither a model nor Argus, so this runs in CI too.
const png = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==', 'base64')
const blank = { reasoning: null, toolName: null, toolCallId: null, toolCalls: null, attachments: [], status: 'complete', error: null, model: null, promptTokens: null, cachedTokens: null, completionTokens: null, thinkingMs: null, durationMs: null, createdAt: new Date().toISOString(), noAccess: false }
const image = (id: string, fileName: string) => ({ id, fileName, size: png.length, truncated: false, kind: 'image', contentType: 'image/png' })
const rows = [
  { repo_id: 1, path_with_namespace: 'platform/codec', path: 'src/frame/decode.c', name: 'DecodeFrame', kind: 'function', line: 118, end_line: 164, signature: 'int DecodeFrame(const uint8_t *buf, size_t len, frame_t *out)', is_public: 1, doc: 'Decodes one frame; returns bytes read or a negative error.' },
  { repo_id: 1, path_with_namespace: 'platform/codec', path: 'include/codec/frame.h', name: 'DecodeFrame', kind: 'prototype', line: 42, end_line: 42, signature: 'int DecodeFrame(const uint8_t *buf, size_t len, frame_t *out);', is_public: 1, doc: null },
]
const file = { repo_id: 1, path_with_namespace: 'platform/codec', path: 'include/codec/frame.h', lang: 'c', size: 220, content: '#pragma once\n#include <stdint.h>\n\ntypedef struct { uint32_t id; uint16_t len; } frame_t;\n\nint DecodeFrame(const uint8_t *buf, size_t len, frame_t *out);\n', truncated: false }
const call = (id: string, name: string, args: object) => ({ id, function: { name, arguments: JSON.stringify(args) } })
const argusChat = {
  id: '00000000-0000-4000-8000-00000000a7a5', title: 'Where is DecodeFrame?', thinking: 'off', useArgus: true, model: null, systemPrompt: null, temperature: null, topP: null, maxTokens: null,
  currentLeafId: 'a2', createdAt: new Date().toISOString(), updatedAt: new Date().toISOString(),
  messages: [
    { ...blank, id: 'q1', parentId: null, role: 'user', content: 'Where is DecodeFrame defined, and does it match these screenshots?', attachments: [image('11111111-1111-4111-8111-111111111111', 'before.png'), image('22222222-2222-4222-8222-222222222222', 'after.png')] },
    { ...blank, id: 'a1', parentId: 'q1', role: 'assistant', content: '', toolCalls: [call('c1', 'find_symbol', { name: 'DecodeFrame' }), call('c2', 'search_code', { query: 'DecodeFrame' }), call('c3', 'get_file', { repo_id: 1, path: 'include/codec/frame.h' })] },
    { ...blank, id: 't1', parentId: 'a1', role: 'tool', toolCallId: 'c1', toolName: 'find_symbol', content: JSON.stringify(rows), durationMs: 132 },
    { ...blank, id: 't2', parentId: 't1', role: 'tool', toolCallId: 'c2', toolName: 'search_code', content: JSON.stringify([{ repo_id: 1, path_with_namespace: 'platform/codec', path: 'src/frame/decode.c', rank: -4.1, snippet: '…n = [DecodeFrame](buf + off, len - off, &f); if (n < 0) return n;…' }]), durationMs: 58 },
    { ...blank, id: 't3', parentId: 't2', role: 'tool', toolCallId: 'c3', toolName: 'get_file', content: JSON.stringify(file), durationMs: 41 },
    { ...blank, id: 'a2', parentId: 't3', role: 'assistant', content: '`DecodeFrame` is defined in `src/frame/decode.c` (lines 118–164) and declared in `include/codec/frame.h`.', model: 'Test-Model' },
  ],
}

async function serveArgusChat(page: Page) {
  await page.route('**/api/chat/config', async (route) => {
    const res = await route.fetch()
    await route.fulfill({ response: res, json: { ...(await res.json()), argus: true, gitlabUrl: 'https://gitlab.example.com' } })
  })
  await page.route(`**/api/chat/conversations/${argusChat.id}`, (route) => route.fulfill({ json: argusChat }))
  await page.route('**/api/chat/attachments/*/content', (route) => route.fulfill({ body: png, contentType: 'image/png' }))
}

test.describe("Argus's answers and images", () => {
  for (const theme of ['light', 'dark'] as const) {
    test(`are shown for what they are (${theme})`, async ({ page, isMobile }, info) => {
      const errors = watchConsole(page)
      await withTheme(page, theme)
      await serveArgusChat(page)
      await page.goto(`/chat/${argusChat.id}`)
      const answer = page.getByRole('region', { name: 'Answer' })
      await answer.getByRole('button', { name: /Find symbol.*2 results/ }).click()
      const link = answer.getByRole('link', { name: /platform\/codec › src\/frame\/decode\.c:118–164/ })
      await expect(link).toHaveAttribute('href', 'https://gitlab.example.com/platform/codec/-/blob/HEAD/src/frame/decode.c#L118-164')
      await answer.getByRole('button', { name: /Search code/ }).click()
      await expect(answer.locator('mark', { hasText: 'DecodeFrame' })).toBeVisible()
      await expectAccessible(page, info, `argus-${theme}`)
      await screenshot(page, info, `chat-argus-${theme}${isMobile ? '-phone' : ''}`)

      await page.getByRole('button', { name: /^Files \(\d+\)$/ }).click()
      const panel = page.getByRole('complementary', { name: 'Files' })
      await panel.getByRole('button', { name: /frame\.h.*read by Argus/ }).click()
      await expect(panel.getByRole('link', { name: /Open in GitLab/ })).toBeVisible()
      await expect(panel.getByRole('figure', { name: 'Code: include/codec/frame.h' })).toContainText('frame_t')
      await page.keyboard.press('Escape')

      await page.getByRole('button', { name: 'View before.png' }).click()
      const viewer = page.getByRole('dialog', { name: 'before.png' })
      await expect(viewer.getByRole('img', { name: 'before.png' })).toBeVisible()
      await viewer.getByRole('button', { name: 'Next image' }).click()
      await expect(page.getByRole('dialog', { name: 'after.png' })).toBeVisible()
      await expectAccessible(page, info, `image-viewer-${theme}`)
      await screenshot(page, info, `image-viewer-${theme}${isMobile ? '-phone' : ''}`)
      await page.keyboard.press('Escape')
      await expect(page.getByRole('dialog')).toHaveCount(0)
      expect(errors).toEqual([])
    })
  }
})

// Persian beside English: each block reads in its own direction, code stays left to right.
const rtlChat = {
  ...argusChat,
  id: '00000000-0000-4000-8000-0000000f0a51', title: 'خلاصه مخزن', useArgus: false, currentLeafId: 'r2',
  messages: [
    { ...blank, id: 'r1', parentId: null, role: 'user', content: 'این مخزن چه کاری انجام می‌دهد؟' },
    {
      ...blank, id: 'r2', parentId: 'r1', role: 'assistant', model: 'Test-Model',
      content: '## خلاصه\n\nاین مخزن یک **کتابخانه** برای خواندن فریم‌ها است و تابع `DecodeFrame` را دارد.\n\n- خواندن فریم\n- بررسی طول\n\n> نکته: خطاها منفی هستند.\n\n```c\nint n = DecodeFrame(buf, len, &f);\n```\n\nThe English summary stays left to right.',
    },
  ],
}

test.describe('right-to-left text', () => {
  test('a Persian question and answer read right to left, code left to right', async ({ page, isMobile }, info) => {
    await page.route(`**/api/chat/conversations/${rtlChat.id}`, (route) => route.fulfill({ json: rtlChat }))
    await page.goto(`/chat/${rtlChat.id}`)
    const direction = (l: Locator) => l.evaluate((e) => getComputedStyle(e).direction)
    const answer = page.getByRole('region', { name: 'Answer' })
    await expect(answer.getByRole('heading', { name: 'خلاصه' })).toBeVisible()
    expect(await direction(page.getByRole('region', { name: 'You' }).getByText('این مخزن چه کاری'))).toBe('rtl')
    expect(await direction(answer.getByText('این مخزن یک'))).toBe('rtl')
    expect(await direction(answer.getByRole('list'))).toBe('rtl')
    expect(await direction(answer.locator('blockquote'))).toBe('rtl')
    expect(await direction(answer.getByText('The English summary'))).toBe('ltr')
    expect(await direction(answer.getByRole('figure', { name: /^Code/ }))).toBe('ltr')
    await page.getByRole('textbox', { name: 'Message' }).fill('سلام')
    expect(await direction(page.getByRole('textbox', { name: 'Message' }))).toBe('rtl')
    await expectAccessible(page, info, 'rtl')
    await screenshot(page, info, `chat-rtl${isMobile ? '-phone' : ''}`)
  })
})

// A diagram is drawn in the answer; a broken one shows its code and why.
const diagramChat = {
  ...argusChat,
  id: '00000000-0000-4000-8000-0000000d1a90', title: 'How a frame is decoded', useArgus: false, currentLeafId: 'd2',
  messages: [
    { ...blank, id: 'd1', parentId: null, role: 'user', content: 'Draw how a frame flows through the decoder.' },
    {
      ...blank, id: 'd2', parentId: 'd1', role: 'assistant', model: 'Test-Model',
      content: 'The data flow:\n\n```mermaid\nflowchart LR\n  accTitle: Frame data flow\n  A["read(buf)"] --> B{header ok?}\n  B -->|yes| C[DecodeFrame]\n  B -->|no| D[error]\n  C --> E[(frame_t)]\n```\n\nAnd one that does not parse:\n\n```mermaid\nflowchart LR\n  A --> (oops\n```',
    },
  ],
}

test.describe('diagrams', () => {
  for (const theme of ['light', 'dark'] as const) {
    test(`a Mermaid diagram is drawn in the answer, its code a click away (${theme})`, async ({ page, isMobile }, info) => {
      const errors = watchConsole(page)
      await withTheme(page, theme)
      await page.route(`**/api/chat/conversations/${diagramChat.id}`, (route) => route.fulfill({ json: diagramChat }))
      await page.goto(`/chat/${diagramChat.id}`)
      const answer = page.getByRole('region', { name: 'Answer' })
      const figure = answer.getByRole('figure', { name: 'Diagram: mermaid' })
      const drawing = figure.frameLocator('iframe[title="Frame data flow"]')
      await expect(drawing.getByText('DecodeFrame')).toBeVisible({ timeout: 20_000 })
      // As tall as the drawing: not the 160px it starts at, not cut.
      await expect.poll(async () => (await figure.locator('iframe').boundingBox())?.height ?? 0).toBeGreaterThan(60)
      await expect(answer.getByText('The diagram could not be drawn')).toBeVisible()
      await expect(answer.getByRole('figure', { name: 'Code: mermaid' })).toContainText('A --> (oops')
      await expectAccessible(page, info, `diagram-${theme}`)
      await screenshot(page, info, `chat-diagram-${theme}${isMobile ? '-phone' : ''}`)

      await figure.getByRole('button', { name: 'Code', exact: true }).click()
      await expect(answer.getByRole('figure', { name: 'Code: mermaid' }).first()).toContainText('accTitle: Frame data flow')
      await answer.getByRole('button', { name: 'Diagram', exact: true }).click()
      await expect(figure.frameLocator('iframe').getByText('DecodeFrame')).toBeVisible()
      expect(errors).toEqual([])
    })
  }
})

// The model asked before going on: the person answers on the card, and that is their next message.
const askChat = {
  ...argusChat,
  id: '00000000-0000-4000-8000-0000000a5c00', title: 'Plan the service', useArgus: false, currentLeafId: 'k3',
  messages: [
    { ...blank, id: 'k1', parentId: null, role: 'user', content: 'Plan a small service for me.' },
    {
      ...blank, id: 'k2', parentId: 'k1', role: 'assistant', model: 'Test-Model', content: 'Two things first.',
      toolCalls: [call('c1', 'ask_user', {
        questions: [
          { question: 'Which database?', options: [{ label: 'PostgreSQL', description: 'Already in the stack' }, { label: 'SQLite', description: 'One file, no server' }] },
          { question: 'Which parts?', options: ['Sign-in', 'Search', 'Billing'], multiple: true },
        ],
      })],
    },
    { ...blank, id: 'k3', parentId: 'k2', role: 'tool', toolCallId: 'c1', toolName: 'ask_user', content: 'The questions are shown to the person.' },
  ],
}

test.describe('questions from the model', () => {
  test('are answered on a card, and the answers are the next message', async ({ page, isMobile }, info) => {
    const errors = watchConsole(page)
    let sent: { content: string; parentId?: string } | null = null
    await page.route(`**/api/chat/conversations/${askChat.id}`, (route) => route.fulfill({ json: askChat }))
    await page.route(`**/api/chat/conversations/${askChat.id}/messages`, async (route) => {
      sent = route.request().postDataJSON()
      await route.fulfill({ status: 409, json: { error: 'not in this test' } })
    })
    await page.goto(`/chat/${askChat.id}`)
    const card = page.getByRole('region', { name: 'Questions for you' })
    const send = card.getByRole('button', { name: 'Send answers' })
    await expect(send).toBeDisabled()
    await card.getByRole('radio', { name: /PostgreSQL/ }).check()
    await card.getByRole('checkbox', { name: 'Sign-in' }).check()
    await card.getByRole('checkbox', { name: 'Search' }).check()
    await card.getByRole('textbox', { name: 'Your own answer: Which parts?' }).fill('Audit log')
    await expectAccessible(page, info, 'questions')
    await screenshot(page, info, `chat-questions${isMobile ? '-phone' : ''}`)
    await send.click()
    await expect.poll(() => sent).toEqual({ content: 'Which database? PostgreSQL\nWhich parts? Sign-in, Search, Audit log', attachments: [], parentId: 'k3', root: false })
    expect(errors.filter((e) => !e.includes('409'))).toEqual([])
  })
})

// Sub-agents did two parts side by side; the card shows each part's result.
const agentsChat = {
  ...argusChat,
  id: '00000000-0000-4000-8000-0000000a9e75', title: 'Two repositories', useArgus: false, currentLeafId: 'g4',
  messages: [
    { ...blank, id: 'g1', parentId: null, role: 'user', content: 'How do codec and driver-shim log errors?' },
    {
      ...blank, id: 'g2', parentId: 'g1', role: 'assistant', model: 'Test-Model', content: '',
      toolCalls: [call('c1', 'delegate', { tasks: [{ title: 'codec errors', instructions: 'Find how platform/codec logs errors.' }, { title: 'driver-shim errors', instructions: 'Find how driver-shim logs errors.' }] })],
    },
    {
      ...blank, id: 'g3', parentId: 'g2', role: 'tool', toolCallId: 'c1', toolName: 'delegate', durationMs: 8400,
      content: JSON.stringify([
        { title: 'codec errors', result: 'Through `log_error()` in `src/log.c`, with an error code.', tool_calls: 3 },
        { title: 'driver-shim errors', result: 'With `pr_err` in `shim/main.c`.', tool_calls: 2 },
      ]),
    },
    { ...blank, id: 'g4', parentId: 'g3', role: 'assistant', model: 'Test-Model', content: 'codec uses `log_error()`; driver-shim uses `pr_err`.' },
  ],
}

test.describe('sub-agents', () => {
  test("each part's result is shown on the card", async ({ page }, info) => {
    await page.route(`**/api/chat/conversations/${agentsChat.id}`, (route) => route.fulfill({ json: agentsChat }))
    await page.goto(`/chat/${agentsChat.id}`)
    const answer = page.getByRole('region', { name: 'Answer' })
    await answer.getByRole('button', { name: /Sub-agents/ }).click()
    const parts = answer.getByRole('list', { name: 'What each sub-agent found' })
    await expect(parts.getByRole('listitem')).toHaveCount(2)
    await expect(parts.getByRole('listitem').first()).toContainText('codec errors3 tool calls')
    await expect(parts.getByRole('listitem').last()).toContainText('pr_err')
    await expectAccessible(page, info, 'sub-agents')
  })
})

test.describe('chat with the model', () => {
  test.skip(!live, 'needs the deployed stack (E2E_CHAT=1)')
  test.setTimeout(180_000)

  test('an answer streams, is kept, and survives a reload', async ({ page }) => {
    const errors = watchConsole(page)
    await page.goto('/chat')
    await thinking(page, 'No thinking')
    await ask(page, 'Reply with exactly the word: pineapple')
    const answer = page.getByRole('region', { name: 'Answer' }).last()
    await expect(answer).toContainText(/pineapple/i, { timeout: 120_000 })
    await done(page)
    await expect(answer.getByText(/\d.* in · .* out/)).toBeVisible()
    await expect(page).toHaveURL(/\/chat\/[0-9a-f-]{36}$/)
    await page.reload()
    await expect(page.getByRole('region', { name: 'Answer' }).last()).toContainText(/pineapple/i)
    await expect(page).toHaveTitle(/^Reply with exactly the word: pineapple ·/)
    expect(errors).toEqual([])
  })

  test('thinking is shown while it happens and folds after', async ({ page }) => {
    await page.goto('/chat')
    await thinking(page, 'Quick')
    await ask(page, 'Is 91 a prime number? Answer yes or no.')
    await expect(page.getByRole('region', { name: 'Answer' }).last().getByText(/Thought for|Thinking…/)).toBeVisible({ timeout: 120_000 })
    await done(page)
  })

  test('stop keeps what was written, and answering again makes a second version', async ({ page }) => {
    await page.goto('/chat')
    await thinking(page, 'No thinking')
    await ask(page, 'Count from 1 to 400, one number per line, no other text.')
    const answer = page.getByRole('region', { name: 'Answer' }).last()
    await expect(answer).toContainText('10', { timeout: 120_000 })
    await page.getByRole('button', { name: 'Stop' }).click()
    await expect(answer.getByText('Stopped.')).toBeVisible({ timeout: 30_000 })
    await expect(answer).not.toContainText('400')
    await answer.getByRole('button', { name: 'Answer again' }).click()
    await page.getByRole('menuitem', { name: 'Answer again' }).click()
    // Stopped once the second answer has begun. (Stopped before it exists, there
    // is no second version, and the first stays on screen.)
    await expect(answer.getByText('Stopped.')).toBeHidden()
    await expect(answer).toContainText('3', { timeout: 120_000 })
    await page.getByRole('button', { name: 'Stop' }).click()
    await expect(answer.getByText('Stopped.')).toBeVisible({ timeout: 30_000 })
    const versions = page.getByRole('navigation', { name: 'Answer versions' })
    await expect(versions).toHaveText(/2 \/ 2/, { timeout: 30_000 })
    await versions.getByRole('button', { name: 'Previous answer version' }).click()
    await expect(versions).toHaveText(/1 \/ 2/)
  })

  test('editing a question keeps both versions', async ({ page }) => {
    await page.goto('/chat')
    await thinking(page, 'No thinking')
    await ask(page, 'Reply with exactly the word: apple')
    await done(page)
    await page.getByRole('region', { name: 'You' }).last().hover()
    await page.getByRole('button', { name: 'Edit question' }).click()
    await page.getByRole('textbox', { name: 'Edit your question' }).fill('Reply with exactly the word: cherry')
    await page.getByRole('region', { name: 'You' }).getByRole('button', { name: 'Send' }).click()
    await expect(page.getByRole('region', { name: 'Answer' }).last()).toContainText(/cherry/i, { timeout: 120_000 })
    await done(page)
    await expect(page.getByRole('navigation', { name: 'Question versions' })).toHaveText(/2 \/ 2/)
  })

  test('a Word document and an Excel workbook are read as their text', async ({ page }) => {
    await page.goto('/chat')
    await thinking(page, 'No thinking')
    const fixtures = new URL('./fixtures/', import.meta.url).pathname
    await page.getByLabel('Attach files').setInputFiles([`${fixtures}plan.docx`, `${fixtures}budget.xlsx`])
    await expect(page.getByText('plan.docx', { exact: true })).toBeVisible()
    await expect(page.getByText('budget.xlsx', { exact: true })).toBeVisible()
    await ask(page, "What is the project's code name, and what is the Platform team's budget? Reply as: NAME, NUMBER")
    const answer = page.getByRole('region', { name: 'Answer' }).last()
    await expect(answer).toContainText('AMBER-FALCON', { timeout: 120_000 })
    await expect(answer).toContainText(/4,?200/)
    await done(page)
    await page.getByRole('button', { name: /^Files \(\d+\)$/ }).click()
    const panel = page.getByRole('complementary', { name: 'Files' })
    await panel.getByRole('button', { name: /budget\.xlsx/ }).click()
    await expect(panel).toContainText('## Sheet: Budget')
  })

  test('an attached file is read, and is in the Files panel', async ({ page }) => {
    await page.goto('/chat')
    await thinking(page, 'No thinking')
    await page.getByLabel('Attach files').setInputFiles({ name: 'secret.txt', mimeType: 'text/plain', buffer: Buffer.from('The launch code word is TANGERINE-42.\n') })
    await expect(page.getByText('secret.txt')).toBeVisible()
    await ask(page, 'What is the launch code word in the attached file? Reply with it only.')
    await expect(page.getByRole('region', { name: 'Answer' }).last()).toContainText('TANGERINE-42', { timeout: 120_000 })
    await done(page)
    await expect(page.getByRole('region', { name: 'You' }).last().getByText('secret.txt')).toBeVisible()
    await page.getByRole('button', { name: /^Files \(\d+\)$/ }).click()
    const panel = page.getByRole('complementary', { name: 'Files' })
    await panel.getByRole('button', { name: /secret\.txt/ }).click()
    await expect(panel).toContainText('TANGERINE-42')
  })

  test('a code block is highlighted, can be copied and downloaded, and opens in the Files panel', async ({ page, context }, info) => {
    await context.grantPermissions(['clipboard-read', 'clipboard-write'])
    await page.goto('/chat')
    await thinking(page, 'No thinking')
    await ask(page, 'Reply with only this, exactly, including the three backticks on their own lines:\n```python title="hello.py"\nprint("hello world")\n```')
    await done(page)
    const code = page.getByRole('region', { name: 'Answer' }).last().getByRole('figure', { name: /^Code:/ }).first()
    await expect(code.locator('.hljs-built_in, .hljs-string').first()).toBeVisible()
    await code.getByRole('button', { name: 'Copy code' }).click()
    await expect(code.getByRole('button', { name: 'Copied' })).toBeVisible()
    expect(await page.evaluate(() => navigator.clipboard.readText())).toContain('print')
    const download = page.waitForEvent('download')
    await code.getByRole('button', { name: /^Download / }).click()
    expect((await download).suggestedFilename()).toMatch(/\.py$/)
    await screenshot(page, info, 'chat-code')
  })

  test('a page the model writes runs in the Files panel, and its code is a tab away', async ({ page }, info) => {
    await page.goto('/chat')
    await thinking(page, 'No thinking')
    await ask(page, 'Reply with only this, exactly, including the three backticks on their own lines:\n```html\n<!doctype html><html><body><h1 id="t">waiting</h1><script>document.getElementById("t").textContent = "PREVIEW " + (40 + 2)</script></body></html>\n```')
    await done(page)
    const code = page.getByRole('region', { name: 'Answer' }).last().getByRole('figure', { name: 'Code: html' })
    await code.getByRole('button', { name: 'Preview this page' }).click()
    const panel = page.getByRole('complementary', { name: 'Files' })
    await expect(panel.frameLocator('iframe[title^="Preview of"]').getByRole('heading', { name: 'PREVIEW 42' })).toBeVisible()
    await screenshot(page, info, 'chat-preview')
    await panel.getByRole('tab', { name: 'Code' }).click()
    await expect(panel.getByRole('figure', { name: /^Code: snippet-\d+\.html$/ })).toContainText('PREVIEW')
  })

  test('chats are listed, searchable, renamed and deleted', async ({ page, isMobile }) => {
    await page.goto('/chat')
    await thinking(page, 'No thinking')
    await ask(page, 'Reply with the single word: ok')
    await done(page)
    const name = `Renamed ${Date.now()}`
    let list = await chatList(page, isMobile)
    const current = list.locator('a[aria-current="page"]').locator('..')
    await current.hover()
    await current.getByRole('button', { name: /^Actions for / }).click()
    await page.getByRole('menuitem', { name: 'Rename' }).click()
    await list.getByRole('textbox', { name: 'Chat name' }).fill(name)
    await list.getByRole('textbox', { name: 'Chat name' }).press('Enter')
    await list.getByRole('searchbox', { name: 'Search chats' }).fill(name)
    await expect(list.getByRole('link', { name })).toBeVisible()
    await list.getByRole('link', { name }).locator('..').hover()
    await list.getByRole('button', { name: `Actions for ${name}` }).click()
    await page.getByRole('menuitem', { name: 'Delete' }).click()
    await page.getByRole('alertdialog').getByRole('button', { name: 'Delete' }).click()
    await expect(page).toHaveURL(/\/chat$/)
    list = await chatList(page, isMobile)
    await list.getByRole('searchbox', { name: 'Search chats' }).fill(name)
    await expect(list.getByRole('link', { name })).toHaveCount(0)
  })

  test("a chat's instructions reach the model", async ({ page }) => {
    await page.goto('/chat')
    await thinking(page, 'No thinking')
    await page.getByRole('button', { name: 'Chat settings' }).click()
    await page.getByLabel('Instructions').fill('End every answer with the word BANANA in capitals.')
    await page.getByRole('button', { name: 'Apply' }).click()
    await ask(page, 'Say hello.')
    await expect(page.getByRole('region', { name: 'Answer' }).last()).toContainText('BANANA', { timeout: 120_000 })
    await done(page)
  })
})

// With or without a model: an answer, or the reason there is none, ends each turn.
test.describe('organising chats', () => {
  test.setTimeout(180_000)

  async function turn(page: Page, text: string) {
    await ask(page, text)
    await expect(page).toHaveURL(/\/chat\/[0-9a-f-]{36}$/)
    await expect(page.getByRole('region', { name: 'Answer' }).last().getByRole('button', { name: 'Copy answer' })).toBeVisible({ timeout: 120_000 })
  }

  test('a long title never scrolls the list sideways; a chat forks, archives, comes back and is deleted', async ({ page, isMobile }) => {
    await page.goto('/chat')
    if (live) await thinking(page, 'No thinking')
    await turn(page, `Reply with the single word: ok ${Date.now()}`)
    const original = page.url()
    const name = `Organise ${Date.now()} ${'a very long chat title that keeps going '.repeat(4)}`.slice(0, 190)

    let list = await chatList(page, isMobile)
    const item = list.locator('a[aria-current="page"]').locator('..')
    await item.hover()
    await item.getByRole('button', { name: /^Actions for / }).click()
    await page.getByRole('menuitem', { name: 'Rename' }).click()
    await list.getByRole('textbox', { name: 'Chat name' }).fill(name)
    await list.getByRole('textbox', { name: 'Chat name' }).press('Enter')
    await expect(list.getByRole('link', { name })).toBeVisible()
    const sideways = await list.evaluate((nav) => [...nav.querySelectorAll<HTMLElement>('div')].filter((d) => d.scrollWidth > d.clientWidth + 1 && getComputedStyle(d).overflowX !== 'hidden').length)
    expect(sideways, 'boxes in the chat list that scroll sideways').toBe(0)

    // Fork the whole chat from the list.
    await list.getByRole('link', { name }).locator('..').hover()
    await list.getByRole('button', { name: `Actions for ${name}` }).click()
    await page.getByRole('menuitem', { name: 'Fork' }).click()
    await expect(page).not.toHaveURL(original)
    await expect(page.getByText('Forked from')).toBeVisible()
    const fork = page.url()

    // Archive the fork from its header, find it under Archived chats, bring it back.
    await page.getByRole('button', { name: 'Chat actions' }).click()
    await page.getByRole('menuitem', { name: 'Archive' }).click()
    await expect(page.getByText(/This chat is archived/)).toBeVisible()
    list = await chatList(page, isMobile)
    await expect(list.getByRole('link', { name: `${name} (fork)`.slice(0, 200) })).toHaveCount(0)
    await list.getByRole('button', { name: 'Archived chats' }).click()
    await expect(list.getByRole('link', { name: `${name} (fork)`.slice(0, 200) })).toBeVisible()
    await list.getByRole('button', { name: 'All chats' }).click()
    if (isMobile) await page.keyboard.press('Escape')
    await page.getByRole('button', { name: 'Unarchive' }).click()
    await expect(page.getByText(/This chat is archived/)).toHaveCount(0)

    // Delete both.
    for (const url of [fork, original]) {
      await page.goto(url)
      await page.getByRole('button', { name: 'Chat actions' }).click()
      await page.getByRole('menuitem', { name: 'Delete' }).click()
      await page.getByRole('alertdialog').getByRole('button', { name: 'Delete' }).click()
      await expect(page).toHaveURL(/\/chat$/)
    }
  })

  test('the rail jumps between questions, and an answer forks from there', async ({ page, isMobile }) => {
    test.skip(isMobile, 'the rail is for wider screens')
    await page.goto('/chat')
    if (live) await thinking(page, 'No thinking')
    await turn(page, 'Reply with the single word: one')
    await turn(page, 'Reply with the single word: two')
    const rail = page.getByRole('navigation', { name: 'Questions in this chat' })
    await expect(rail.getByRole('button')).toHaveCount(2)
    await rail.getByRole('button', { name: /^Question 1:/ }).click()
    await expect(rail.getByRole('button', { name: /^Question 1:/ })).toHaveAttribute('aria-current', 'location')
    await expect(page.getByRole('region', { name: 'You' }).first()).toBeInViewport()
    await expect(page.getByRole('region', { name: 'You' }).first()).toBeFocused()

    const chat = page.url()
    await page.getByRole('region', { name: 'Answer' }).first().getByRole('button', { name: 'Fork from here' }).click()
    await expect(page).not.toHaveURL(chat)
    await expect(page.getByRole('region', { name: 'You' })).toHaveCount(1)
    for (const url of [page.url(), chat]) {
      await page.goto(url)
      await page.getByRole('button', { name: 'Chat actions' }).click()
      await page.getByRole('menuitem', { name: 'Delete' }).click()
      await page.getByRole('alertdialog').getByRole('button', { name: 'Delete' }).click()
      await expect(page).toHaveURL(/\/chat$/)
    }
  })
})

test.describe('tools', () => {
  test('the composer turns tools on and off for a chat', async ({ page }) => {
    await page.goto('/chat')
    const button = page.getByRole('button', { name: /^Tools: \d+ of \d+ on$/ })
    const before = Number((await button.getAttribute('aria-label'))!.match(/(\d+) of/)![1])
    await button.click()
    await page.getByRole('switch', { name: /Calculator/ }).click()
    await expect(page.getByRole('button', { name: new RegExp(`^Tools: ${before - 1} of`) })).toBeVisible()
  })

  test('the model uses the calculator, and draws with the image tool', async ({ page, request }) => {
    test.skip(!live, 'needs the deployed stack (E2E_CHAT=1)')
    test.setTimeout(300_000)
    await page.goto('/chat')
    await thinking(page, 'No thinking')
    await ask(page, 'Use the calculator tool: what is 123456789 * 987654321? Reply with the number only.')
    const answer = page.getByRole('region', { name: 'Answer' }).last()
    await expect(answer.locator('.tool-name', { hasText: 'Calculate' })).toBeVisible({ timeout: 120_000 })
    await expect(answer).toContainText(/121,?932,?631,?112,?635,?269/, { timeout: 120_000 })
    await done(page)

    const tools = (await (await request.get('/api/chat/config')).json()) as { tools: { id: string }[] }
    test.skip(!tools.tools.some((t) => t.id === 'image'), 'no image model at the gateway')
    await ask(page, 'Use the image tool to draw a small red apple on a white table, 512x512.')
    const pictures = page.getByRole('region', { name: 'Answer' }).last().getByRole('list', { name: 'Pictures' })
    await expect(pictures.getByRole('img')).toBeVisible({ timeout: 240_000 })
    await done(page)
    await pictures.getByRole('button').first().click()
    await expect(page.getByRole('dialog')).toBeVisible()
  })

  test('the model searches the web and reads a page of an allowed site', async ({ page, request }) => {
    test.skip(!live, 'needs the deployed stack (E2E_CHAT=1)')
    test.setTimeout(300_000)
    const headers = { 'X-Requested-With': 'fetch' }
    // On for this chat only (not in new chats), with one site allowed; put back after.
    expect((await request.put('/api/admin/config', { headers, data: { changes: [{ key: 'Web:AllowedSites', value: 'docs.python.org' }] } })).ok()).toBe(true)
    expect((await request.put('/api/admin/tools/web', { headers, data: { enabled: true, audience: 'Everyone', groups: [], onByDefault: false, askFirst: false } })).ok()).toBe(true)
    try {
      await page.goto('/chat')
      await thinking(page, 'No thinking')
      await page.getByRole('button', { name: /^Tools: \d+ of \d+ on$/ }).click()
      await page.getByRole('switch', { name: /^Web/ }).click()
      await page.keyboard.press('Escape')
      await ask(page, 'Use web_search to find the Python documentation for asyncio.gather, open that page with fetch_page, and tell me in one sentence what return_exceptions=True does. Give the page address.')
      const answer = page.getByRole('region', { name: 'Answer' }).last()
      await expect(answer.locator('.tool-name', { hasText: 'Fetch page' }).first()).toBeVisible({ timeout: 180_000 })
      await done(page)
      await expect(answer).toContainText('docs.python.org')
      await expect(answer).toContainText(/exception/i)
    } finally {
      await request.put('/api/admin/tools/web', { headers, data: { enabled: false, audience: 'Everyone', groups: [], onByDefault: false, askFirst: false } })
      await request.put('/api/admin/config', { headers, data: { changes: [{ key: 'Web:AllowedSites', value: null, reset: true }] } })
    }
  })

  test('the model analyses an attached workbook with Python and charts it', async ({ page, request }) => {
    test.skip(!live, 'needs the deployed stack (E2E_CHAT=1)')
    const tools = (await (await request.get('/api/chat/config')).json()) as { tools: { id: string }[] }
    test.skip(!tools.tools.some((t) => t.id === 'python'), 'no Python sandbox (profile sandbox)')
    test.setTimeout(300_000)
    await page.goto('/chat')
    await thinking(page, 'No thinking')
    await page.getByLabel('Attach files').setInputFiles(new URL('./fixtures/budget.xlsx', import.meta.url).pathname)
    await expect(page.getByText('budget.xlsx', { exact: true })).toBeVisible()
    await ask(page, 'Use run_python with pandas to read budget.xlsx, print the total of the Budget column, and save a bar chart of Budget by Team as chart.png. Then tell me the total.')
    const answer = page.getByRole('region', { name: 'Answer' }).last()
    await expect(answer.locator('.tool-name', { hasText: 'Run python' }).first()).toBeVisible({ timeout: 120_000 })
    await expect(answer.getByRole('list', { name: 'Pictures' }).getByRole('img')).toBeVisible({ timeout: 180_000 })
    await done(page)
    await expect(answer).toContainText(/5,?500/)
  })

  test('an interactive chart Python writes opens running in the Files panel', async ({ page, request }) => {
    test.skip(!live, 'needs the deployed stack (E2E_CHAT=1)')
    const tools = (await (await request.get('/api/chat/config')).json()) as { tools: { id: string }[] }
    test.skip(!tools.tools.some((t) => t.id === 'python'), 'no Python sandbox (profile sandbox)')
    test.setTimeout(300_000)
    await page.goto('/chat')
    await thinking(page, 'No thinking')
    await ask(page, 'Use run_python: with plotly.express make a bar chart of fruit counts (apples 3, pears 5, plums 2) titled "Fruit" and save it with fig.write_html("fruit.html"). Then say done.')
    const answer = page.getByRole('region', { name: 'Answer' }).last()
    const made = answer.getByRole('list', { name: 'Files made' })
    await made.getByRole('button', { name: 'Open fruit.html in the Files panel' }).click({ timeout: 180_000 })
    const frame = page.getByRole('complementary', { name: 'Files' }).frameLocator('iframe[title="Preview of fruit.html"]')
    // plotly.js is inside the page (write_html's default): drawn with no network.
    await expect(frame.locator('.plot-container svg.main-svg').first()).toBeVisible({ timeout: 30_000 })
    await expect(frame.getByText('Fruit', { exact: true }).first()).toBeVisible()
  })
})

// With the Argus test fixture up and a person who cannot read eal-core:
//   E2E_ARGUS_USER=dev_beta E2E_ARGUS_PASSWORD=...
test.describe('chat with Argus', () => {
  test.skip(!live || !process.env.E2E_ARGUS_PASSWORD, 'needs the Argus test fixture')
  test.use({ storageState: { cookies: [], origins: [] } })
  test.setTimeout(240_000)

  test('someone without access is told which repository and whom to ask', async ({ page }) => {
    await page.goto('/login')
    await page.getByLabel('Username or email').fill(process.env.E2E_ARGUS_USER ?? 'dev_beta')
    await page.getByLabel('Password').fill(process.env.E2E_ARGUS_PASSWORD!)
    await page.getByRole('button', { name: 'Sign in' }).click()
    await expect(page).not.toHaveURL(/\/login/)
    await page.goto('/chat')
    await thinking(page, 'No thinking')
    await ask(page, "In our organisation's code, where is the function DecodeFrame defined? Look it up.")
    const note = page.getByRole('note')
    await expect(note).toContainText('You do not have access to some of this code.', { timeout: 180_000 })
    await expect(note).toContainText('root/eal-core')
    // Which of Argus's tools the model picks varies; that one ran is what matters.
    await expect(page.getByRole('region', { name: 'Answer' }).last().locator('.tool-name').first()).toBeVisible()
  })
})
