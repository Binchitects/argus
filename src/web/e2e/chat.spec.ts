import { expect, test, type Locator, type Page } from '@playwright/test'
import { promises as fs } from 'node:fs'
import { expectAccessible, screenshot, watchConsole, withTheme } from './helpers.ts'
import { makeZip } from '../src/lib/zip.ts'

/** The smallest PDF with one page and a line of text, offsets and all. */
function onePagePdf(): Buffer {
  const objects = [
    '<< /Type /Catalog /Pages 2 0 R >>',
    '<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
    '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>',
    '<< /Length 44 >>\nstream\nBT /F1 24 Tf 72 720 Td (One page.) Tj ET\nendstream',
    '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>',
  ]
  let out = '%PDF-1.4\n'
  const offsets: number[] = []
  objects.forEach((o, i) => {
    offsets.push(out.length)
    out += `${i + 1} 0 obj\n${o}\nendobj\n`
  })
  const xref = out.length
  out += `xref\n0 ${objects.length + 1}\n0000000000 65535 f \n${offsets.map((o) => `${String(o).padStart(10, '0')} 00000 n \n`).join('')}`
  out += `trailer\n<< /Size ${objects.length + 1} /Root 1 0 R >>\nstartxref\n${xref}\n%%EOF\n`
  return Buffer.from(out, 'latin1')
}

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
    // A model that answers makes this a real chat in someone's list: only where none does.
    const config = await (await page.request.get('/api/chat/config')).json()
    test.skip(config.models?.some((m: { loaded: boolean }) => m.loaded), 'a model is serving here')
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
  await page.route('**/api/chat/attachments/*/content*', (route) => route.fulfill({ body: png, contentType: 'image/png' }))
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
      // All of them in one zip: the pictures, and the file Argus read under its repository.
      const saving = page.waitForEvent('download')
      await panel.getByRole('button', { name: /^Download all \d+ files as a zip$/ }).click()
      const zip = await saving
      expect(zip.suggestedFilename()).toBe('Where is DecodeFrame files.zip')
      const bytes = await fs.readFile(await zip.path())
      expect(bytes.subarray(0, 4).toString('hex')).toBe('504b0304')
      for (const name of ['before.png', 'after.png', 'platform/codec/include/codec/frame.h']) expect(bytes.includes(Buffer.from(name))).toBe(true)
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

// A Word file shown by its pages (drawn in the sandbox), zoomed in the panel and full size.
const docChat = {
  ...argusChat,
  id: '00000000-0000-4000-8000-0000000d0c5a', title: 'The plan', useArgus: false, currentLeafId: 'w2',
  messages: [
    {
      ...blank, id: 'w1', parentId: null, role: 'user', content: 'Summarize the plan.',
      attachments: [{ id: '44444444-4444-4444-8444-444444444444', fileName: 'plan.docx', size: 18_000, truncated: false, kind: 'text', contentType: 'text/plain', original: true }],
    },
    { ...blank, id: 'w2', parentId: 'w1', role: 'assistant', model: 'Test-Model', content: 'The plan has two phases.' },
  ],
}

test.describe('document pages', () => {
  test('a Word file shows as its pages, zoomed in the panel and full size', async ({ page }, info) => {
    const base = '/api/chat/attachments/44444444-4444-4444-8444-444444444444'
    await page.route(`**/api/chat/conversations/${docChat.id}`, (route) => route.fulfill({ json: docChat }))
    await page.route(`**${base}/pages`, (route) => route.fulfill({ json: { total: 23, drawn: 2, pages: [`${base}/pages/1`, `${base}/pages/2`] } }))
    await page.route(`**${base}/pages/*`, (route) => route.fulfill({ body: png, contentType: 'image/png' }))
    await page.goto(`/chat/${docChat.id}`)
    await page.getByRole('button', { name: /^Files \(1\)$/ }).click()
    const panel = page.getByRole('complementary', { name: 'Files' })
    await panel.getByRole('button', { name: /plan\.docx/ }).click()
    const pages = panel.getByRole('list', { name: 'Pages of plan.docx' })
    await expect(pages.getByRole('img')).toHaveCount(2)
    await expect(panel).toContainText('23 pages · the first 2 shown')
    await panel.getByRole('button', { name: 'Zoom in' }).click()
    await expect(panel.getByRole('button', { name: 'Zoom 125%: fit the panel' })).toBeVisible()
    await expectAccessible(page, info, 'document-pages')
    await screenshot(page, info, 'document-pages')
    await pages.getByRole('button', { name: 'Page 2: view full size' }).click()
    const viewer = page.getByRole('dialog', { name: 'plan.docx' })
    await expect(viewer).toContainText('Page 2 of 23')
    await viewer.getByRole('button', { name: 'Zoom in' }).click()
    await expect(viewer.getByRole('button', { name: /^Zoom \d+%: fit to the screen$/ })).toBeVisible()
    await page.keyboard.press('Escape')
    await expect(viewer).toHaveCount(0)
  })
})

// How full the context is, and what fills it: from the last answer's prompt tokens.
const contextChat = {
  ...argusChat,
  id: '00000000-0000-4000-8000-0000000c0e7a', title: 'A long chat', useArgus: false, currentLeafId: 'x2',
  messages: [
    { ...blank, id: 'x1', parentId: null, role: 'user', content: 'Summarize the attached report.' },
    {
      ...blank, id: 'x2', parentId: 'x1', role: 'assistant', model: 'Test-Model', content: 'The report says sales grew.', promptTokens: 21_000, cachedTokens: 0, completionTokens: 400,
      context: { system: 3000, instructions: 300, tools: 5000, summary: 0, files: 11000, you: 200, answers: 800, toolResults: 700 },
    },
  ],
}

test.describe('context gauge', () => {
  test('shows how full the context is and what fills it, and compacts from there', async ({ page }, info) => {
    await page.route(`**/api/chat/conversations/${contextChat.id}`, (route) => route.fulfill({ json: contextChat }))
    let compacted = false
    await page.route(`**/api/chat/conversations/${contextChat.id}/compact`, (route) => {
      compacted = true
      return route.fulfill({ body: 'data: {"type":"done","id":"x2"}\n\n', contentType: 'text/event-stream' })
    })
    await page.goto(`/chat/${contextChat.id}`)
    await page.getByRole('button', { name: /^Context: \d+% full/ }).click()
    const parts = page.getByRole('list', { name: 'What fills it' })
    await expect(parts).toContainText('Files')
    await expect(parts).toContainText('Tool definitions')
    await expect(parts).toContainText('Kept for the answer')
    await expectAccessible(page, info, 'context-gauge')
    await screenshot(page, info, 'context-gauge')
    await page.getByRole('button', { name: 'Compact now' }).click()
    await expect.poll(() => compacted).toBe(true)
  })
})

test.describe('projects', () => {
  test('a project is made with instructions and a file, a chat starts in it, and another moves in', async ({ page, isMobile }, info) => {
    const headers = { 'X-Requested-With': 'e2e' }
    const name = `Codec ${Date.now()}`
    const loose = (await (await page.request.post('/api/chat/conversations', { data: {}, headers })).json()).id
    try {
      await page.goto('/chat')
      const list = await chatList(page, isMobile)
      await list.getByRole('button', { name: 'New project' }).click()
      const dialog = page.getByRole('dialog', { name: 'New project' })
      await dialog.getByLabel('Name').fill(name)
      await dialog.getByRole('button', { name: 'Create' }).click()
      await expect(page.getByRole('heading', { level: 1, name })).toBeVisible()
      await page.getByLabel('Project instructions').fill('Answer as a codec engineer.')
      await page.getByRole('region', { name: 'Instructions' }).getByRole('button', { name: 'Save' }).click()
      await expect(page.getByText('Instructions saved')).toBeVisible()
      await page.getByLabel('Add files to the project').setInputFiles({ name: 'facts.txt', mimeType: 'text/plain', buffer: Buffer.from('The frame header is 12 bytes.\n') })
      await expect(page.getByRole('list', { name: 'Files' })).toContainText('facts.txt')
      await expectAccessible(page, info, 'project')
      await screenshot(page, info, `project${isMobile ? '-phone' : ''}`)

      // A new chat from the project's page is in it.
      await page.getByRole('button', { name: 'New chat in this project' }).click()
      await expect(page).toHaveURL(/\/chat$/)
      if (!isMobile) await expect(page.getByRole('link', { name: `Project: ${name}` })).toBeVisible()

      // Another chat moves in from its menu.
      await page.goto(`/chat/${loose}`)
      await page.getByRole('button', { name: 'Chat actions' }).click()
      await page.getByRole('menuitem', { name: 'Move to project' }).click()
      await page.getByRole('menuitem', { name }).click()
      await expect(page.getByText('Moved to the project')).toBeVisible()
      const moved = await (await page.request.get(`/api/chat/conversations/${loose}`)).json()
      expect(moved.project.name).toBe(name)
    } finally {
      const projects = (await (await page.request.get('/api/projects')).json()) as { id: string; name: string }[]
      for (const p of projects.filter((p) => p.name === name)) await page.request.delete(`/api/projects/${p.id}`, { headers })
      await page.request.delete(`/api/chat/conversations/${loose}`, { headers })
    }
  })
})

test.describe('search', () => {
  test('words found in an answer open the chat at that answer', async ({ page, isMobile }, info) => {
    await serveArgusChat(page)
    let asked = ''
    await page.route('**/api/chat/search?*', (route) => {
      asked = new URL(route.request().url()).search
      return route.fulfill({
        json: [
          { conversationId: argusChat.id, title: argusChat.title, archived: false, messageId: 'a2', where: 'answer', snippet: '`DecodeFrame` is defined in `src/frame/decode.c` (lines 118–164)…', at: new Date().toISOString(), model: 'Test-Model' },
          { conversationId: argusChat.id, title: argusChat.title, archived: false, messageId: null, where: 'title', snippet: argusChat.title, at: new Date().toISOString(), model: null },
        ],
      })
    })
    await page.goto('/chat')
    await (await chatList(page, isMobile)).getByRole('button', { name: 'Advanced search' }).click()
    const dialog = page.getByRole('dialog', { name: 'Search your chats' })
    await dialog.getByRole('button', { name: 'Tool results' }).click()
    await dialog.getByLabel('Search for').fill('decode.c')
    const results = dialog.getByRole('list', { name: 'Results' })
    await expect(results.locator('mark').first()).toHaveText('decode.c')
    expect(asked).toContain('in=title%2Cprompt%2Canswer%2Cfile')
    await expectAccessible(page, info, 'chat-search')
    await screenshot(page, info, `chat-search${isMobile ? '-phone' : ''}`)
    await results.getByRole('button', { name: /defined in/ }).click()
    await expect(page).toHaveURL(new RegExp(`/chat/${argusChat.id}$`))
    await expect(page.locator('[data-message="a2"]')).toHaveClass(/found/)
  })
})

test.describe('export', () => {
  test('a chat downloads as Markdown and as data, and its summary comes from the model', async ({ page }) => {
    await serveArgusChat(page)
    await page.route(`**/api/chat/conversations/${argusChat.id}/summary`, (route) => route.fulfill({ json: { title: argusChat.title, summary: '# DecodeFrame\n\n- Defined in src/frame/decode.c' } }))
    await page.goto(`/chat/${argusChat.id}`)
    const exportAs = async (item: string) => {
      await page.getByRole('button', { name: 'Chat actions' }).click()
      await page.getByRole('menuitem', { name: 'Export' }).click()
      const saving = page.waitForEvent('download')
      await page.getByRole('menuitem', { name: item }).click()
      const file = await saving
      return { name: file.suggestedFilename(), text: (await fs.readFile(await file.path())).toString('utf8') }
    }
    const md = await exportAs('Markdown (.md)')
    expect(md.name).toBe('Where is DecodeFrame.md')
    expect(md.text).toContain('## You\n\nWhere is DecodeFrame defined')
    expect(md.text).toContain('*Attached: before.png, after.png*')
    expect(md.text).toContain('**Find symbol**')
    const json = JSON.parse((await exportAs('Data (.json)')).text)
    expect(json.messages).toHaveLength(argusChat.messages.length)
    const summary = await exportAs('Summary by the model (.md)')
    expect(summary).toEqual({ name: 'Where is DecodeFrame summary.md', text: '# DecodeFrame\n\n- Defined in src/frame/decode.c' })
  })
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

// Sub-agents did two parts side by side; the card shows each part's work, the pictures they drew too.
const drawn = image('33333333-3333-4333-8333-333333333333', 'codec-flow.png')
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
      ...blank, id: 'g3', parentId: 'g2', role: 'tool', toolCallId: 'c1', toolName: 'delegate', durationMs: 8400, attachments: [drawn],
      content: JSON.stringify([
        { title: 'codec errors', result: 'Through `log_error()` in `src/log.c`, with an error code.', tool_calls: 1 },
        { title: 'driver-shim errors', result: 'With `pr_err` in `shim/main.c`.', tool_calls: 0 },
      ]),
      details: {
        agents: [
          {
            title: 'codec errors', instructions: 'Find how platform/codec logs errors.', reasoning: 'Search for the error helper first.', text: 'Through `log_error()` in `src/log.c`, with an error code.',
            steps: [
              { id: 's1', name: 'search_code', arguments: '{"query":"log_error"}', result: '[{"path":"src/log.c","line":12}]', isError: false },
              { id: 's2', name: 'generate_image', arguments: '{"prompt":"codec error flow"}', result: 'Made codec-flow.png.', isError: false, files: [drawn] },
            ],
            error: null, ms: 5200,
          },
          { title: 'driver-shim errors', instructions: 'Find how driver-shim logs errors.', reasoning: '', text: 'With `pr_err` in `shim/main.c`.', steps: [], error: null, ms: 3100 },
        ],
      },
    },
    { ...blank, id: 'g4', parentId: 'g3', role: 'assistant', model: 'Test-Model', content: 'codec uses `log_error()`; driver-shim uses `pr_err`.' },
  ],
}

test.describe('sub-agents', () => {
  test("each part's work is shown on the card as the chat shows its own: thinking, tool calls, pictures, words", async ({ page }, info) => {
    await page.route(`**/api/chat/conversations/${agentsChat.id}`, (route) => route.fulfill({ json: agentsChat }))
    await page.route('**/api/chat/attachments/*/content', (route) => route.fulfill({ body: png, contentType: 'image/png' }))
    await page.goto(`/chat/${agentsChat.id}`)
    const answer = page.getByRole('region', { name: 'Answer' })
    // What the sub-agents drew is the call's: under it, and in the Files panel.
    await expect(answer.getByRole('list', { name: 'Pictures' }).getByRole('img', { name: 'codec-flow.png' })).toBeVisible()
    await answer.getByRole('button', { name: /Sub-agents/ }).click()
    const agents = answer.getByRole('list', { name: 'Sub-agents' })
    await expect(answer).toContainText('2 of 2 done')
    const codec = agents.getByRole('listitem', { name: 'codec errors' })
    const shim = agents.getByRole('listitem', { name: 'driver-shim errors' })
    // Done parts are folded, one under the other; all open at once.
    await expect(codec).toContainText('2 tool calls · 1 file · 5.2 s')
    await expect(codec).not.toContainText('Find how platform/codec logs errors.')
    await answer.getByRole('button', { name: 'Expand all' }).click()
    await expect(codec).toContainText('Find how platform/codec logs errors.')
    await expect(shim).toContainText('pr_err')
    await codec.getByRole('button', { name: /Thought process/ }).click()
    await expect(codec).toContainText('Search for the error helper first.')
    await codec.getByRole('button', { name: /Search code/ }).click()
    await expect(codec).toContainText('src/log.c')
    await expect(codec.getByRole('list', { name: 'Pictures' }).getByRole('img', { name: 'codec-flow.png' })).toBeVisible()
    await screenshot(page, info, 'sub-agents')
    await answer.getByRole('button', { name: 'Collapse all' }).click()
    await expect(shim).not.toContainText('pr_err')
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
    // A workbook shows its pages first; the text the model read is a tab away.
    await panel.getByRole('tab', { name: 'Text' }).click()
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

  test("sub-agents work side by side, and each one's work shows as it happens", async ({ page }, info) => {
    test.skip(!live, 'needs the deployed stack (E2E_CHAT=1)')
    test.setTimeout(300_000)
    // A chat with only these two tools: the model has nothing else to reach for.
    const made = await page.request.post('/api/chat/conversations', { data: { tools: ['agents', 'calculator'] }, headers: { 'X-Requested-With': 'e2e' } })
    await page.goto(`/chat/${(await made.json()).id}`)
    // Small parts on purpose: the model is told to delegate even so (else it may just calculate).
    await ask(page, 'Call the delegate tool once with two sub-agents, even though the parts are small (this is a test of sub-agents): (1) compute 17*23 with the calculator, (2) compute 2^20 with the calculator. Then give me both results.')
    const answer = page.getByRole('region', { name: 'Answer' }).last()
    const agents = answer.getByRole('list', { name: 'Sub-agents' })
    await expect(agents.getByRole('listitem')).toHaveCount(2, { timeout: 120_000 })
    await expect(agents.getByRole('listitem').first()).toContainText(/tool call/, { timeout: 120_000 })
    await done(page)
    await expect(answer).toContainText(/391/)
    await expect(answer).toContainText(/1,?048,?576/)
    await screenshot(page, info, 'sub-agents-live')
    // After a reload, their work is still there (kept with the call).
    await page.reload()
    await page.getByRole('region', { name: 'Answer' }).last().getByRole('button', { name: /Sub-agents 2 parts/ }).click()
    await expect(page.getByRole('list', { name: 'Sub-agents' }).getByRole('listitem').first()).toContainText(/tool call/)
    expect((await page.request.delete(`/api/chat/conversations/${page.url().split('/').pop()}`, { headers: { 'X-Requested-With': 'e2e' } })).status()).toBe(204)
  })

  test('sub-agents draw pictures side by side: each is shown and kept in the Files panel', async ({ page, request }) => {
    test.skip(!live, 'needs the deployed stack (E2E_CHAT=1)')
    test.setTimeout(600_000)
    const tools = (await (await request.get('/api/chat/config')).json()) as { tools: { id: string }[] }
    test.skip(!tools.tools.some((t) => t.id === 'image'), 'no image model at the gateway')
    const made = await page.request.post('/api/chat/conversations', { data: { tools: ['agents', 'image'] }, headers: { 'X-Requested-With': 'e2e' } })
    const id = (await made.json()).id
    await page.goto(`/chat/${id}`)
    await ask(page, 'Call the delegate tool once with two sub-agents (this is a test of sub-agents): one draws a red apple, the other a blue cup, each with the image tool at 512x512.')
    const answer = page.getByRole('region', { name: 'Answer' }).last()
    // Each picture is in the Files list as soon as its sub-agent has it, before the answer is over.
    await expect(page.getByRole('button', { name: /^Files \([12]\)$/ })).toBeVisible({ timeout: 480_000 })
    await done(page)
    const pictures = answer.getByRole('list', { name: 'Pictures' }).last()
    await expect(pictures.getByRole('img')).toHaveCount(2)
    await page.reload()
    await expect(page.getByRole('button', { name: 'Files (2)' })).toBeVisible()
    await expect(page.getByRole('region', { name: 'Answer' }).last().getByRole('list', { name: 'Pictures' }).last().getByRole('img')).toHaveCount(2)
    expect((await page.request.delete(`/api/chat/conversations/${id}`, { headers: { 'X-Requested-With': 'e2e' } })).status()).toBe(204)
  })

  test('the context gauge splits a real answer by what filled it', async ({ page }) => {
    test.skip(!live, 'needs the deployed stack (E2E_CHAT=1)')
    test.setTimeout(180_000)
    await page.goto('/chat')
    await thinking(page, 'No thinking')
    await page.getByLabel('Attach files').setInputFiles({ name: 'facts.txt', mimeType: 'text/plain', buffer: Buffer.from('The sky is green on Tuesdays.\n'.repeat(200)) })
    await ask(page, 'What colour is the sky on Tuesdays, says the file? One word.')
    await done(page)
    await page.getByRole('button', { name: /^Context: \d+% full/ }).click()
    const parts = page.getByRole('list', { name: 'What fills it' })
    for (const kind of ['System prompt and tool notes', 'Files', 'Your messages', 'Answers']) await expect(parts).toContainText(kind)
    await page.keyboard.press('Escape')
    expect((await page.request.delete(`/api/chat/conversations/${page.url().split('/').pop()}`, { headers: { 'X-Requested-With': 'e2e' } })).status()).toBe(204)
  })

  test('a Word file and a PDF are drawn as pages in the sandbox', async ({ page }) => {
    test.skip(!live, 'needs the deployed stack (E2E_CHAT=1)')
    test.setTimeout(180_000)
    const headers = { 'X-Requested-With': 'e2e' }
    const xml = (body: string) => Buffer.from(`<?xml version="1.0" encoding="UTF-8" standalone="yes"?>${body}`)
    const word = 'http://schemas.openxmlformats.org/wordprocessingml/2006/main'
    const docx = Buffer.from(await (await makeZip([
      { name: '[Content_Types].xml', data: xml('<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>') },
      { name: '_rels/.rels', data: xml('<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>') },
      { name: 'word/document.xml', data: xml(`<w:document xmlns:w="${word}"><w:body><w:p><w:r><w:t>The plan has two phases.</w:t></w:r></w:p><w:p><w:r><w:br w:type="page"/><w:t>Phase two.</w:t></w:r></w:p></w:body></w:document>`) },
    ])).arrayBuffer())
    for (const [name, mimeType, buffer] of [['plan.docx', 'application/vnd.openxmlformats-officedocument.wordprocessingml.document', docx], ['one.pdf', 'application/pdf', onePagePdf()]] as const) {
      const made = await page.request.post('/api/chat/attachments', { multipart: { file: { name, mimeType, buffer } }, headers })
      expect(made.ok(), await made.text()).toBe(true)
      const id = (await made.json()).id
      const pages = await page.request.get(`/api/chat/attachments/${id}/pages`, { headers, timeout: 150_000 })
      expect(pages.ok(), await pages.text()).toBe(true)
      const { total, drawn, pages: urls } = await pages.json()
      expect(total).toBe(name === 'plan.docx' ? 2 : 1)
      expect(drawn).toBe(total)
      const first = await page.request.get(urls[0], { headers })
      expect(first.headers()['content-type']).toBe('image/jpeg')
      expect((await first.body()).length).toBeGreaterThan(2000)
    }
  })

  test('answer now stops a long thought and the model answers', async ({ page }) => {
    test.skip(!live, 'needs the deployed stack (E2E_CHAT=1)')
    test.setTimeout(240_000)
    await page.goto('/chat')
    await thinking(page, 'Deep think')
    await ask(page, 'Is 1000003 a prime number? Think it through very carefully first. Then reply with only yes or no.')
    const answer = page.getByRole('region', { name: 'Answer' }).last()
    await answer.getByRole('button', { name: 'Answer now' }).click({ timeout: 120_000 })
    await done(page)
    await expect(answer).toContainText(/Thought for .*, cut short/)
    await expect(answer.locator('.md')).toContainText(/yes|no|prime/i)
    expect((await page.request.delete(`/api/chat/conversations/${page.url().split('/').pop()}`, { headers: { 'X-Requested-With': 'e2e' } })).status()).toBe(204)
  })

  test('an answer that finishes after the page left is in the bell', async ({ page }) => {
    test.skip(!live, 'needs the deployed stack (E2E_CHAT=1)')
    test.setTimeout(240_000)
    const headers = { 'X-Requested-With': 'e2e' }
    const id = (await (await page.request.post('/api/chat/conversations', { data: { tools: [], thinking: 'off' }, headers })).json()).id
    // The page asks, and is gone two seconds later; the answer goes on without it.
    await page.request.post(`/api/chat/conversations/${id}/messages`, { data: { content: 'Count from 1 to 40 in words, one per line.', root: true }, headers, timeout: 2_000 }).catch(() => null)
    await expect
      .poll(async () => ((await (await page.request.get('/api/notifications')).json()).items as { kind: string; link: string }[]).some((n) => n.kind === 'answer' && n.link === `/chat/${id}`), {
        timeout: 180_000,
      })
      .toBe(true)
    await page.goto('/chat')
    await page.getByRole('button', { name: /^Notifications, \d+ new$/ }).click()
    await page.getByRole('dialog', { name: 'Notifications' }).getByRole('button', { name: /^Answer ready:/ }).first().click()
    await expect(page).toHaveURL(new RegExp(`/chat/${id}$`))
    await expect(page.getByRole('region', { name: 'Answer' }).last()).toContainText(/forty/i)
    expect((await page.request.delete(`/api/chat/conversations/${id}`, { headers })).status()).toBe(204)
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

  test('deep research plans and sends sub-agents to read the web', async ({ page, request, isMobile }) => {
    test.skip(!live, 'needs the deployed stack (E2E_CHAT=1)')
    test.skip(isMobile, 'minutes of the one model: one browser is enough')
    // The whole report takes 20 minutes and more on one GPU (docs/plan.md, N4): this checks the
    // research starts as it should (a plan, sub-agents searching the web), then stops it.
    test.setTimeout(600_000)
    const headers = { 'X-Requested-With': 'fetch' }
    expect((await request.put('/api/admin/config', { headers, data: { changes: [{ key: 'Web:AllowedSites', value: 'docs.python.org' }] } })).ok()).toBe(true)
    expect((await request.put('/api/admin/tools/web', { headers, data: { enabled: true, audience: 'Everyone', groups: [], onByDefault: false, askFirst: false } })).ok()).toBe(true)
    try {
      await page.goto('/chat')
      await thinking(page, 'No thinking')
      await page.getByRole('button', { name: 'Deep research' }).click()
      await ask(page, 'What do asyncio.gather and asyncio.TaskGroup each do in Python, and when should I use which? Use only docs.python.org pages.')
      const answer = page.getByRole('region', { name: 'Answer' }).last()
      await expect(answer.locator('.tool-name', { hasText: 'Sub-agents' }).first()).toBeVisible({ timeout: 300_000 })
      const agents = answer.getByRole('list', { name: 'Sub-agents' })
      await expect(agents.getByRole('listitem').nth(1)).toBeVisible()
      await expect(agents.locator('.tool-name', { hasText: /Web search|Fetch page/ }).first()).toBeVisible({ timeout: 300_000 })
      await page.getByRole('button', { name: 'Stop' }).click()
      await expect(page.getByRole('button', { name: 'Send' })).toBeVisible({ timeout: 60_000 })
      expect((await page.request.delete(`/api/chat/conversations/${page.url().split('/').pop()}`, { headers: { 'X-Requested-With': 'e2e' } })).status()).toBe(204)
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
