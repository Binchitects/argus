import { expect, test, type Page } from '@playwright/test'
import { expectAccessible, screenshot, watchConsole, withTheme } from './helpers.ts'

// With a real model behind the gateway (the deployed stack): E2E_CHAT=1.
// Without one (CI), only the "no gateway" behaviour is checked.
const live = process.env.E2E_CHAT === '1'

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
    await expect(page.getByRole('button', { name: 'Stop' })).toBeVisible()
    await page.getByRole('button', { name: 'Stop' }).click()
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
