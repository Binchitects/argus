import { expect, test } from '@playwright/test'
import { noGateway, watchConsole } from './helpers.ts'

/**
 * A Code Arena session's chat, open on the web, follows the session without a reload: what it adds, its turn (the page
 * says Code Arena is answering, and lets the person write again once the turn ends) and its compaction. Code Arena here
 * is its own API (/api/code-arena) with a new person's key, as `code-arena login` is given it; no model answers.
 */
test('the open web chat follows a Code Arena session: its messages, its turn and its compaction', async ({ browser, baseURL, playwright }) => {
  test.skip(noGateway, 'makes a person')
  test.setTimeout(120_000)
  const admin = await browser.newContext({ ignoreHTTPSErrors: true, baseURL, storageState: 'e2e/.auth/state.json' })
  const name = `e2ec${Date.now().toString(36)}`
  const made = await admin.request.post('/api/admin/people', { data: { userName: name, email: `${name}@example.test` }, headers: { 'X-Requested-With': 'e2e' } })
  expect(made.ok()).toBe(true)
  const { id: person, password, apiKey } = await made.json()
  const ctx = await browser.newContext({ ignoreHTTPSErrors: true, baseURL, storageState: { cookies: [], origins: [] } })
  const arena = await playwright.request.newContext({
    baseURL, ignoreHTTPSErrors: true, extraHTTPHeaders: { Authorization: `Bearer ${apiKey}`, 'X-Requested-With': 'code-arena' },
  })
  try {
    // Signed in, the sign-in notice accepted (a stack may have one) and "What's new" seen: nothing covers the chat.
    const web = ctx.request
    expect((await web.post('/api/auth/login', { data: { userName: name, password, redirect: '/' }, headers: { 'X-Requested-With': 'e2e' } })).ok()).toBe(true)
    const notice = await (await web.get('/api/account/notice')).json()
    if (notice.on && notice.acceptedVersion !== notice.version) {
      expect((await web.post('/api/account/notice/accept', { data: { version: notice.version }, headers: { 'X-Requested-With': 'e2e' } })).ok()).toBe(true)
    }
    expect((await web.put('/api/account/whats-new', { data: { release: '9999.0' }, headers: { 'X-Requested-With': 'e2e' } })).ok()).toBe(true)

    // Code Arena's session: its chat, made with its first turn.
    const chat: string = (await (await arena.post('/api/code-arena/chats', { data: { ref: name, place: '/work/shop', model: 'model-a' } })).json()).id
    let key = 0
    /** Code Arena's send after `parent`: the ids Arena gave the messages. */
    const send = async (parent: string | null, messages: { role: string; content: string }[], summary?: { upTo: string; text: string }) => {
      const sent = await arena.post(`/api/code-arena/chats/${chat}/messages`, {
        data: { parent, messages: messages.map((m) => ({ ...m, ref: `${name}:${key++}` })), summary },
      })
      expect(sent.status(), await sent.text()).toBe(200)
      return (await sent.json()).ids as string[]
    }
    const first = await send(null, [{ role: 'user', content: 'Fix the parser' }, { role: 'assistant', content: 'Fixed the parser.' }])

    const page = await ctx.newPage()
    const errors = watchConsole(page)
    await page.goto(`/chat/${chat}`)
    await expect(page.getByText('A Code Arena session in')).toBeVisible()
    await expect(page.getByText('Fixed the parser.')).toBeVisible()
    const marks = page.getByRole('note', { name: 'Chat compacted' })
    await expect(marks).toHaveCount(0)

    // A turn starts in Code Arena: the page says so, and shows its question as it comes.
    expect((await arena.post(`/api/code-arena/chats/${chat}/turn`)).ok()).toBe(true)
    const question = await send(first[1]!, [{ role: 'user', content: 'Run the long build' }])
    const answering = page.getByText('Code Arena is answering in this chat')
    await expect(answering).toBeVisible({ timeout: 15_000 })
    await expect(page.getByText('Run the long build')).toBeVisible({ timeout: 15_000 })

    // The turn ends: its answer shows, and the person may write again.
    const answer = await send(question[0]!, [{ role: 'assistant', content: 'The build passes.' }])
    expect((await arena.delete(`/api/code-arena/chats/${chat}/turn`)).ok()).toBe(true)
    await expect(page.getByText('The build passes.')).toBeVisible({ timeout: 15_000 })
    await expect(answering).toBeHidden({ timeout: 15_000 })

    // Code Arena compacts (its /compact): no message comes with it, and the page marks it all the same.
    await send(answer[0]!, [], { upTo: first[1]!, text: 'The parser was fixed; the build is next.' })
    await expect(marks).toHaveCount(1, { timeout: 15_000 })
    await marks.getByRole('button', { name: 'Show summary' }).click()
    await expect(page.getByText('The parser was fixed; the build is next.')).toBeVisible()
    expect(errors).toEqual([])
  } finally {
    await arena.dispose()
    await ctx.close()
    await admin.request.delete(`/api/admin/people/${person}`, { headers: { 'X-Requested-With': 'e2e' } })
    await admin.close()
  }
})
