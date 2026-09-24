import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { fakeApi, member, renderApp } from '@/test/utils'
import { blank } from './live'
import type { ChatConfig, Conversation, Message } from './types'

vi.mock('./api', async (original) => ({
  ...(await original<typeof import('./api')>()),
  // XMLHttpRequest (for upload progress) has no server in tests.
  uploadFile: vi.fn(async (file: File, onProgress: (p: number) => void) => {
    onProgress(1)
    const image = file.type.startsWith('image/')
    return { id: `att-${file.name}`, fileName: file.name, size: file.size, truncated: false, kind: image ? 'image' : 'text', contentType: file.type }
  }),
}))

const config: ChatConfig = {
  model: 'Main-Model',
  models: [
    { name: 'Main-Model', context: 32768, maxOutput: 8192, vision: false, tools: true, thinking: true, prices: { input: 0.2, cachedInput: 0.02, output: 0.8 } },
    { name: 'Eyes-Model', context: 32768, maxOutput: 4096, vision: true, tools: true, thinking: false, prices: { input: 1, cachedInput: 0.1, output: 2 } },
  ],
  presets: [{ level: 'xhigh', label: 'Deep think' }, { level: 'off', label: 'No thinking' }],
  defaultThinking: 'xhigh',
  argus: true,
  gitlabUrl: null,
  maxUploadBytes: 20 * 1024 * 1024,
  imageTypes: ['image/png'],
}

const conversation = (over: Partial<Conversation> = {}): Conversation => ({
  id: 'c1', title: 'New chat', thinking: null, useArgus: true, model: null, systemPrompt: null, temperature: null, topP: null, maxTokens: null,
  currentLeafId: null, createdAt: '', updatedAt: '', messages: [], ...over,
})

const msg = (id: string, parentId: string | null, role: Message['role'], over: Partial<Message> = {}): Message => ({ ...blank(id, role, parentId), ...over })

/** `saved` is what the server holds once the answer is over: the page reloads it, as it does for real. */
function backend(opts: { events?: object[]; saved?: Message[]; hang?: boolean; start?: Conversation; config?: Partial<ChatConfig>; extra?: Parameters<typeof fakeApi>[1] }) {
  let answered = false
  const saved = opts.saved ?? []
  const leaf = saved.at(-1)?.id ?? null
  return fakeApi(member, {
    'GET /api/chat/config': () => ({ json: { ...config, ...opts.config } }),
    'GET /api/chat/conversations': () => ({ json: [] }),
    'POST /api/chat/conversations': () => ({ status: 201, json: conversation() }),
    'GET /api/chat/conversations/c1': () => ({ json: answered ? conversation({ messages: saved, currentLeafId: leaf }) : (opts.start ?? conversation()) }),
    'POST /api/chat/conversations/c1/messages': () => {
      answered = true
      return { events: opts.events ?? [], hang: opts.hang }
    },
    'POST /api/chat/conversations/c1/regenerate': () => {
      answered = true
      return { events: opts.events ?? [], hang: opts.hang }
    },
    ...opts.extra,
  })
}

async function ask(text: string) {
  const box = await screen.findByRole('textbox', { name: 'Message' })
  await userEvent.type(box, text)
  await userEvent.keyboard('{Enter}')
}

const answer = [
  { type: 'question', id: 'q1', parentId: null },
  { type: 'title', title: 'Hello there' },
  { type: 'assistant', id: 'a1', parentId: 'q1', model: 'Main-Model' },
  { type: 'reasoning', text: 'Let me think.' },
  { type: 'thought', ms: 2300 },
  { type: 'content', text: 'Hi! Here is code:\n\n```python title="hello.py"\nprint("hi")\n```' },
  { type: 'usage', prompt: 1000, cached: 400, completion: 200, thinkingMs: 2300, durationMs: 5000 },
  { type: 'done', id: 'a1' },
]
const answered = [
  msg('q1', null, 'user', { content: 'hello there' }),
  msg('a1', 'q1', 'assistant', { content: 'Hi! Here is code:\n\n```python title="hello.py"\nprint("hi")\n```', reasoning: 'Let me think.', thinkingMs: 2300, durationMs: 5000, model: 'Main-Model', promptTokens: 1000, cachedTokens: 400, completionTokens: 200 }),
]

describe('chat', () => {
  it('a new chat is made with the chosen settings, and the answer streams in with its thinking', async () => {
    const calls = backend({ events: answer, saved: answered })
    const { router } = renderApp('/chat')
    await userEvent.click(await screen.findByRole('button', { name: /^Model:/ }))
    await userEvent.click(await screen.findByRole('menuitem', { name: /Eyes-Model/ }))
    await ask('hello there')
    await waitFor(() => expect(router.state.location.pathname).toBe('/chat/c1'))
    expect(calls.find((c) => c.method === 'POST' && c.path === '/api/chat/conversations')?.body).toMatchObject({ model: 'Eyes-Model', useArgus: true })
    expect(calls.find((c) => c.path === '/api/chat/conversations/c1/messages')?.body).toEqual({ content: 'hello there', attachments: [], root: true })
    const a = await screen.findByRole('region', { name: 'Answer' })
    expect(await within(a).findByText('Thought for 2.3 s')).toBeInTheDocument()
    expect(within(a).getByText(/Here is code/)).toBeInTheDocument()
    // Tokens and cost from the model's prices: (600*0.2 + 400*0.02 + 200*0.8) / 1e6.
    expect(within(a).getByText(/1 K in · 200 out/)).toBeInTheDocument()
    expect(within(a).getByText('· $0.00029')).toBeInTheDocument()
  })

  it('code the model writes is in the Files panel, and a code block can be copied and downloaded', async () => {
    backend({ start: conversation({ messages: answered, currentLeafId: 'a1', title: 'Hello there' }) })
    renderApp('/chat/c1')
    const code = await screen.findByRole('figure', { name: 'Code: hello.py' })
    expect(within(code).getByRole('button', { name: 'Download hello.py' })).toBeInTheDocument()
    await userEvent.click(within(code).getByRole('button', { name: 'Open hello.py in the Files panel' }))
    const panel = await screen.findByRole('complementary', { name: 'Files' })
    expect(within(panel).getByRole('figure', { name: 'Code: hello.py' })).toBeInTheDocument()
    // On a narrow screen the panel opens over the page (a sheet): the button stays pressed behind it.
    expect(screen.getByRole('button', { name: 'Files (1)', hidden: true })).toHaveAttribute('aria-pressed', 'true')
  })

  it('shows tool use as a card, and a no-access notice naming whom to ask', async () => {
    backend({
      events: [
        { type: 'question', id: 'q1', parentId: null },
        { type: 'assistant', id: 'a1', parentId: 'q1', model: 'Main-Model' },
        { type: 'tool_call', id: 'call_1', name: 'find_symbol', arguments: '{"name":"SecretThing"}' },
        { type: 'tool_result', id: 'call_1', messageId: 't1', name: 'find_symbol', text: 'Nothing you have access to matches this.\n- root/secret (maintainers: @alice)', isError: false, noAccess: true, durationMs: 420 },
        { type: 'assistant', id: 'a2', parentId: 't1', model: 'Main-Model' },
        { type: 'content', text: 'Ask @alice.' },
        { type: 'done', id: 'a2' },
      ],
      hang: true,
    })
    renderApp('/chat')
    await ask('where is SecretThing')
    const a = await screen.findByRole('region', { name: 'Answer' })
    expect(await within(a).findByText('Find symbol')).toBeInTheDocument()
    expect(within(a).getByText('SecretThing')).toBeInTheDocument()
    expect(within(a).getByRole('note')).toHaveTextContent('root/secret (maintainers: @alice)')
  })

  it("Argus's answers link to the code in GitLab, and a file it read is in the Files panel", async () => {
    const rows = [
      { repo_id: 1, path_with_namespace: 'group/app', path: 'src/parse.c', name: 'ParseHeader', kind: 'function', line: 10, end_line: 24, signature: 'int ParseHeader(const char *buf)', is_public: 1, doc: null },
      { repo_id: 1, path_with_namespace: 'group/app', path: 'include/parse.h', name: 'ParseHeader', kind: 'prototype', line: 3, end_line: 3, signature: 'int ParseHeader(const char *buf);', is_public: 1, doc: null },
    ]
    const file = { repo_id: 1, path_with_namespace: 'group/app', path: 'src/parse.c', lang: 'c', size: 40, content: 'int ParseHeader(const char *buf) {\n  return 0;\n}\n', truncated: false }
    const call = (id: string, name: string, args: object) => ({ id, function: { name, arguments: JSON.stringify(args) } })
    const messages = [
      msg('q1', null, 'user', { content: 'where is ParseHeader?' }),
      msg('a1', 'q1', 'assistant', { toolCalls: [call('c1', 'find_symbol', { name: 'ParseHeader', branch: 'dev' }), call('c2', 'get_file', { repo_id: 1, path: 'src/parse.c' })] }),
      msg('t1', 'a1', 'tool', { toolCallId: 'c1', toolName: 'find_symbol', content: JSON.stringify(rows), durationMs: 120 }),
      msg('t2', 't1', 'tool', { toolCallId: 'c2', toolName: 'get_file', content: JSON.stringify(file), durationMs: 80 }),
      msg('a2', 't2', 'assistant', { content: 'In src/parse.c.' }),
    ]
    backend({ start: conversation({ messages, currentLeafId: 'a2' }), config: { gitlabUrl: 'https://gitlab.example.com' } })
    renderApp('/chat/c1')
    const a = await screen.findByRole('region', { name: 'Answer' })
    await userEvent.click(await within(a).findByRole('button', { name: /Find symbol.*2 results/ }))
    const places = within(a).getByRole('list', { name: 'Places in the code' })
    const links = within(places).getAllByRole('link')
    expect(links[0]).toHaveAttribute('href', 'https://gitlab.example.com/group/app/-/blob/dev/src/parse.c#L10-24')
    expect(links[0]).toHaveAttribute('target', '_blank')
    expect(links[0]).toHaveTextContent('group/app › src/parse.c:10–24')
    expect(links[1]).toHaveAttribute('href', 'https://gitlab.example.com/group/app/-/blob/dev/include/parse.h#L3')
    expect(within(places).getByText('prototype')).toBeInTheDocument()
    // The signature is highlighted as C.
    expect(places.querySelector('.hljs-type, .hljs-keyword')).not.toBeNull()
    await userEvent.click(within(a).getAllByRole('button', { name: 'Raw answer' })[0]!)
    expect(within(a).getByRole('figure', { name: 'Code: json' })).toHaveTextContent('"end_line": 24')

    await userEvent.click(screen.getByRole('button', { name: /^Files \(1\)$/, hidden: true }))
    const panel = await screen.findByRole('complementary', { name: 'Files' })
    await userEvent.click(within(panel).getByRole('button', { name: /parse\.c.*read by Argus from group\/app/ }))
    expect(within(panel).getByRole('link', { name: /Open in GitLab/ })).toHaveAttribute('href', 'https://gitlab.example.com/group/app/-/blob/HEAD/src/parse.c')
    expect(within(panel).getByRole('figure', { name: 'Code: src/parse.c' })).toHaveTextContent('return 0;')
  })

  it("a tool's error stays its own words, and without a GitLab address nothing links", async () => {
    const messages = [
      msg('q1', null, 'user', { content: 'find it' }),
      msg('a1', 'q1', 'assistant', { toolCalls: [{ id: 'c1', function: { name: 'search_code', arguments: '{"query":"buf"}' } }, { id: 'c2', function: { name: 'get_file', arguments: '{}' } }] }),
      msg('t1', 'a1', 'tool', { toolCallId: 'c1', toolName: 'search_code', content: JSON.stringify([{ repo_id: 1, path_with_namespace: 'g/a', path: 'a.c', rank: -1, snippet: 'x = [buf][0];' }]) }),
      msg('t2', 't1', 'tool', { toolCallId: 'c2', toolName: 'get_file', content: 'No file at repo_id=1, path=\'x\'.', status: 'failed' }),
      msg('a2', 't2', 'assistant', { content: 'Found.' }),
    ]
    backend({ start: conversation({ messages, currentLeafId: 'a2' }) })
    renderApp('/chat/c1')
    const a = await screen.findByRole('region', { name: 'Answer' })
    await userEvent.click(await within(a).findByRole('button', { name: /Search code.*1 result/ }))
    const places = within(a).getByRole('list', { name: 'Places in the code' })
    expect(within(places).queryByRole('link')).toBeNull()
    expect(within(places).getByText('buf', { selector: 'mark' })).toBeInTheDocument()
    expect(places).toHaveTextContent('x = buf[0];')
    await userEvent.click(within(a).getByRole('button', { name: /Get file.*Failed/ }))
    expect(within(a).getByText(/No file at repo_id=1/)).toBeInTheDocument()
  })

  it('images open at full size, with arrows between those sent together', async () => {
    const image = (id: string, fileName: string) => ({ id, fileName, size: 2048, truncated: false, kind: 'image' as const, contentType: 'image/png' })
    const messages = [msg('q1', null, 'user', { content: 'compare', attachments: [image('i1', 'before.png'), image('i2', 'after.png')] }), msg('a1', 'q1', 'assistant', { content: 'Done.' })]
    backend({ start: conversation({ messages, currentLeafId: 'a1' }) })
    renderApp('/chat/c1')
    await userEvent.click(await screen.findByRole('button', { name: 'View before.png' }))
    const viewer = await screen.findByRole('dialog', { name: 'before.png' })
    expect(within(viewer).getByText(/2.0 KiB · 1 of 2/)).toBeInTheDocument()
    expect(within(viewer).getByRole('link', { name: 'Download before.png' })).toHaveAttribute('download', 'before.png')
    await userEvent.click(within(viewer).getByRole('button', { name: 'Next image' }))
    expect(await screen.findByRole('dialog', { name: 'after.png' })).toBeInTheDocument()
    await userEvent.keyboard('{ArrowRight}')
    expect(await screen.findByRole('dialog', { name: 'before.png' })).toBeInTheDocument()
    await userEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Show at actual size' }))
    expect(within(screen.getByRole('dialog')).getByRole('button', { name: 'Fit to the screen' })).toBeInTheDocument()
    await userEvent.keyboard('{Escape}')
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
  })

  it('stop keeps what was written and gives the box back', async () => {
    backend({
      events: [{ type: 'question', id: 'q1', parentId: null }, { type: 'assistant', id: 'a1', parentId: 'q1', model: 'Main-Model' }, { type: 'content', text: 'partial answer' }],
      hang: true,
      saved: [msg('q1', null, 'user', { content: 'long one' }), msg('a1', 'q1', 'assistant', { content: 'partial answer', status: 'stopped' })],
    })
    renderApp('/chat')
    await ask('long one')
    await userEvent.click(await screen.findByRole('button', { name: 'Stop' }))
    expect(await screen.findByText('Stopped.')).toBeInTheDocument()
    expect(screen.getByText('partial answer')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Send' })).toBeInTheDocument()
  })

  it('shows why an answer failed', async () => {
    backend({
      events: [{ type: 'question', id: 'q1', parentId: null }, { type: 'assistant', id: 'a1', parentId: 'q1', model: 'Main-Model' }, { type: 'error', message: 'You have used all your credit. Ask an admin to raise it.' }],
      saved: [msg('q1', null, 'user', { content: 'hi' }), msg('a1', 'q1', 'assistant', { status: 'failed', error: 'You have used all your credit. Ask an admin to raise it.' })],
    })
    renderApp('/chat')
    await ask('hi')
    expect(await screen.findByRole('alert')).toHaveTextContent('You have used all your credit.')
  })

  it('a message that never reached the server goes back into the box', async () => {
    backend({ extra: { 'POST /api/chat/conversations/c1/messages': () => ({ offline: true }) } })
    renderApp('/chat')
    await ask('is anyone there')
    expect(await screen.findByRole('alert')).toHaveTextContent('did not reach the server')
    await waitFor(() => expect(screen.getByRole('textbox', { name: 'Message' })).toHaveValue('is anyone there'))
  })

  it('editing a question sends a new version beside it, and the arrows switch between them', async () => {
    const two = [
      msg('q1', null, 'user', { content: 'first try' }),
      msg('a1', 'q1', 'assistant', { content: 'answer one' }),
      msg('q2', null, 'user', { content: 'second try' }),
      msg('a2', 'q2', 'assistant', { content: 'answer two' }),
    ]
    const calls = backend({
      start: conversation({ messages: two.slice(0, 2), currentLeafId: 'a1' }),
      events: [{ type: 'question', id: 'q2', parentId: null }, { type: 'assistant', id: 'a2', parentId: 'q2', model: 'Main-Model' }, { type: 'content', text: 'answer two' }, { type: 'done', id: 'a2' }],
      saved: two,
      extra: { 'PUT /api/chat/conversations/c1/leaf': () => ({ json: { currentLeafId: 'a1' } }) },
    })
    renderApp('/chat/c1')
    await userEvent.click(await screen.findByRole('button', { name: 'Edit question' }))
    const box = screen.getByRole('textbox', { name: 'Edit your question' })
    await userEvent.clear(box)
    await userEvent.type(box, 'second try')
    await userEvent.click(within(box.closest('form')!).getByRole('button', { name: 'Send' }))
    expect(await screen.findByText('answer two')).toBeInTheDocument()
    expect(calls.find((c) => c.path === '/api/chat/conversations/c1/messages')?.body).toEqual({ content: 'second try', attachments: [], root: true })
    const versions = await screen.findByRole('navigation', { name: 'Question versions' })
    expect(versions).toHaveTextContent('2 / 2')
    await userEvent.click(within(versions).getByRole('button', { name: 'Previous question version' }))
    expect(await screen.findByText('answer one')).toBeInTheDocument()
    expect(calls.find((c) => c.method === 'PUT')?.body).toEqual({ messageId: 'q1' })
  })

  it('answering again can use another thinking level', async () => {
    const calls = backend({ start: conversation({ messages: answered, currentLeafId: 'a1' }), events: [{ type: 'done', id: 'x' }], saved: answered })
    renderApp('/chat/c1')
    await userEvent.click(await screen.findByRole('button', { name: 'Answer again' }))
    await userEvent.click(await screen.findByRole('menuitem', { name: 'No thinking' }))
    await waitFor(() => expect(calls.find((c) => c.path === '/api/chat/conversations/c1/regenerate')?.body).toEqual({ messageId: 'q1', thinking: 'off' }))
  })

  it('an image is previewed, and a model that cannot see says so', async () => {
    backend({})
    renderApp('/chat')
    const input = await screen.findByLabelText('Attach files')
    const png = new File([new Uint8Array([137, 80, 78, 71])], 'shot.png', { type: 'image/png' })
    vi.stubGlobal('URL', Object.assign(URL, { createObjectURL: () => 'blob:preview', revokeObjectURL: () => {} }))
    await userEvent.upload(input, png)
    expect(await screen.findByRole('img', { name: 'shot.png' })).toHaveAttribute('src', 'blob:preview')
    expect(screen.getByText(/Main-Model cannot see images/)).toBeInTheDocument()
  })

  it('instructions and parameters are checked and saved for the chat', async () => {
    const calls = backend({ start: conversation({ messages: answered, currentLeafId: 'a1' }), extra: { 'PATCH /api/chat/conversations/c1': () => ({ status: 204 }) } })
    renderApp('/chat/c1')
    await userEvent.click(await screen.findByRole('button', { name: 'Chat settings' }))
    await userEvent.type(screen.getByLabelText('Instructions'), 'Be brief.')
    await userEvent.type(screen.getByLabelText('Temperature'), '5')
    await userEvent.click(screen.getByRole('button', { name: 'Apply' }))
    expect(await screen.findByText('Temperature is from 0 to 2.')).toBeInTheDocument()
    await userEvent.clear(screen.getByLabelText('Temperature'))
    await userEvent.type(screen.getByLabelText('Temperature'), '0.2')
    await userEvent.click(screen.getByRole('button', { name: 'Apply' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'PATCH')?.body).toEqual({ systemPrompt: 'Be brief.', temperature: 0.2, topP: -1, maxTokens: -1 }))
  })

  it('model HTML never runs, links open safely, and maths is typeset', async () => {
    const hostile = [msg('q1', null, 'user', { content: 'x' }), msg('a1', 'q1', 'assistant', { content: '<img src=x onerror="window.hacked=1"><script>window.hacked=2</script> [site](https://example.test) and $E=mc^2$' })]
    backend({ start: conversation({ messages: hostile, currentLeafId: 'a1' }) })
    renderApp('/chat/c1')
    const link = await screen.findByRole('link', { name: 'site' })
    expect(link).toHaveAttribute('target', '_blank')
    expect(link.getAttribute('rel')).toContain('noopener')
    const a = screen.getByRole('region', { name: 'Answer' })
    expect(a.querySelector('script')).toBeNull()
    expect(a.querySelector('[onerror]')).toBeNull()
    expect(a.querySelector('.katex')).not.toBeNull()
    expect((window as { hacked?: number }).hacked).toBeUndefined()
  })
})
