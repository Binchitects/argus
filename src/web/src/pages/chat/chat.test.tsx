import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import type { Me } from '@/lib/api'
import type { AnswerTrace } from '@/pages/admin/traces-api'
import { admin, fakeApi, member, renderApp } from '@/test/utils'
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
    { name: 'Main-Model', context: 32768, maxOutput: 8192, vision: false, tools: true, thinking: true, loaded: true, prices: { input: 0.2, cachedInput: 0.02, output: 0.8 } },
    { name: 'Eyes-Model', context: 32768, maxOutput: 4096, vision: true, tools: true, thinking: false, loaded: true, prices: { input: 1, cachedInput: 0.1, output: 2 } },
  ],
  presets: [{ level: 'xhigh', label: 'Deep think' }, { level: 'off', label: 'No thinking' }],
  defaultThinking: 'xhigh',
  argus: true,
  tools: [
    { id: 'argus', title: 'Argus', description: 'Searches the code you can read in GitLab.', icon: 'search-code', onByDefault: true, askFirst: false },
    { id: 'calculator', title: 'Calculator', description: 'Exact arithmetic.', icon: 'calculator', onByDefault: true, askFirst: true },
  ],
  gitlabUrl: null,
  maxUploadBytes: 20 * 1024 * 1024,
  imageTypes: ['image/png'],
}

const conversation = (over: Partial<Conversation> = {}): Conversation => ({
  id: 'c1', title: 'New chat', thinking: null, tools: ['argus', 'calculator'], useArgus: true, model: null, systemPrompt: null, temperature: null, topP: null, maxTokens: null,
  currentLeafId: null, archivedAt: null, forkedFrom: null, createdAt: '', updatedAt: '', messages: [], ...over,
})

const msg = (id: string, parentId: string | null, role: Message['role'], over: Partial<Message> = {}): Message => ({ ...blank(id, role, parentId), ...over })

/** `saved` is what the server holds once the answer is over: the page reloads it, as it does for real. */
function backend(opts: { events?: object[]; saved?: Message[]; hang?: boolean; start?: Conversation; config?: Partial<ChatConfig>; extra?: Parameters<typeof fakeApi>[1]; me?: Me }) {
  let answered = false
  const saved = opts.saved ?? []
  const leaf = saved.at(-1)?.id ?? null
  return fakeApi(opts.me ?? member, {
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
    'POST /api/chat/conversations/c1/stop': () => ({ status: 202 }),
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
    expect(calls.find((c) => c.method === 'POST' && c.path === '/api/chat/conversations')?.body).toEqual({ model: 'Eyes-Model' })
    expect(calls.find((c) => c.path === '/api/chat/conversations/c1/messages')?.body).toEqual({ content: 'hello there', attachments: [], root: true })
    const a = await screen.findByRole('region', { name: 'Answer' })
    expect(await within(a).findByText('Thought for 2.3 s')).toBeInTheDocument()
    expect(within(a).getByText(/Here is code/)).toBeInTheDocument()
    // Tokens and cost from the model's prices: (600*0.2 + 400*0.02 + 200*0.8) / 1e6.
    expect(within(a).getByText(/1 K in · 200 out/)).toBeInTheDocument()
    expect(within(a).getByText('· $0.000288')).toBeInTheDocument()
  })

  it('a chat that chose no model shows the one that answers: the default, not the first listed', async () => {
    // An admin loaded the second model: the first is listed, not loaded; the default is the second.
    backend({ config: { model: 'Eyes-Model', models: [{ ...config.models[0], loaded: false }, config.models[1]] } })
    renderApp('/chat')
    expect(await screen.findByRole('button', { name: 'Model: Eyes-Model' })).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Model: Eyes-Model' }))
    expect(await screen.findByRole('menuitem', { name: /Eyes-Model/ })).not.toHaveAttribute('aria-disabled')
    expect(screen.getByRole('menuitem', { name: /Main-Model/ })).toHaveAttribute('aria-disabled', 'true')
  })

  it('an answer waiting its turn says how many go first', async () => {
    backend({ events: [{ type: 'question', id: 'q1', parentId: null }, { type: 'queued', ahead: 2 }], hang: true })
    renderApp('/chat')
    await ask('busy day')
    expect(await screen.findByText('Waiting for your turn: 2 answers ahead of you.')).toBeInTheDocument()
  })

  it('a model that is not loaded is listed but cannot be chosen', async () => {
    backend({ config: { models: [config.models[0], { ...config.models[1], loaded: false }] } })
    renderApp('/chat')
    await userEvent.click(await screen.findByRole('button', { name: /^Model:/ }))
    const eyes = await screen.findByRole('menuitem', { name: /Eyes-Model/ })
    expect(eyes).toHaveAttribute('aria-disabled', 'true')
    expect(within(eyes).getByText('Not loaded now')).toBeInTheDocument()
    expect(screen.getByText(/answers once an admin loads it/)).toBeInTheDocument()
  })

  it('a model that loads when asked can be chosen, and says its first answer waits', async () => {
    backend({ config: { models: [config.models[0], { ...config.models[1], loaded: false, onRequest: true }] } })
    renderApp('/chat')
    await userEvent.click(await screen.findByRole('button', { name: /^Model:/ }))
    const eyes = await screen.findByRole('menuitem', { name: /Eyes-Model/ })
    expect(eyes).not.toHaveAttribute('aria-disabled', 'true')
    expect(within(eyes).getByText('Loads when asked')).toBeInTheDocument()
    expect(screen.getByText(/first answer wait while it loads/)).toBeInTheDocument()
    await userEvent.click(eyes)
    expect(await screen.findByRole('button', { name: 'Model: Eyes-Model' })).toHaveTextContent('Loads when asked')
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

  it('a page the model writes has a Preview that runs it sandboxed, beside its code', async () => {
    const page = '<!doctype html><html><body><h1>Hi</h1></body></html>'
    const saved = [answered[0]!, { ...answered[1]!, content: 'Here:\n\n```html\n' + page + '\n```\n\nAnd a plain one:\n\n```js\nconsole.log(1)\n```' }]
    backend({ start: conversation({ messages: saved, currentLeafId: saved.at(-1)!.id, title: 'A page' }) })
    renderApp('/chat/c1')
    const html = await screen.findByRole('figure', { name: 'Code: html' })
    // Only code that can run offers it.
    expect(screen.getByRole('figure', { name: 'Code: javascript' })).not.toHaveTextContent('Preview')
    await userEvent.click(within(html).getByRole('button', { name: 'Preview this page' }))
    const panel = await screen.findByRole('complementary', { name: 'Files' })
    const frame = within(panel).getByTitle('Preview of snippet-1.html')
    expect(frame).toHaveAttribute('src', '/preview.html')
    expect(frame).toHaveAttribute('sandbox', 'allow-scripts allow-forms allow-modals')
    await userEvent.click(within(panel).getByRole('tab', { name: 'Code' }))
    expect(within(panel).getByRole('figure', { name: 'Code: snippet-1.html' })).toHaveTextContent('<h1>Hi</h1>')
    expect(within(panel).queryByTitle('Preview of snippet-1.html')).not.toBeInTheDocument()
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

  it('the chat list forks, archives and brings back chats, and never scrolls sideways', async () => {
    const chat = { id: 'c1', title: 'Hello there', updatedAt: new Date().toISOString() }
    let archived = false
    const calls = backend({
      start: conversation({ messages: answered, currentLeafId: 'a1', title: 'Hello there' }),
      extra: {
        'GET /api/chat/conversations': (_b, _i, url) => ({ json: (url.searchParams.get('archived') === 'true') === archived ? [chat] : [] }),
        'PATCH /api/chat/conversations/c1': (body) => {
          archived = (body as { archived: boolean }).archived
          return { status: 204 }
        },
        'POST /api/chat/conversations/c1/fork': () => ({ status: 201, json: { id: 'c2', title: 'Hello there (fork)' } }),
        'GET /api/chat/conversations/c2': () => ({ json: conversation({ id: 'c2', title: 'Hello there (fork)', messages: answered, currentLeafId: 'a1', forkedFrom: { id: 'c1', title: 'Hello there' } }) }),
      },
    })
    const { router } = renderApp('/chat/c1')
    const list = await screen.findByRole('navigation', { name: 'Chats' })
    const link = await within(list).findByRole('link', { name: 'Hello there' })
    expect(link).toHaveClass('truncate')
    expect(list.querySelector('.overflow-x-hidden')).not.toBeNull()

    await userEvent.click(within(list).getByRole('button', { name: 'Actions for Hello there' }))
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Archive' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'PATCH')?.body).toEqual({ archived: true }))
    expect(await screen.findByText('Chat archived')).toBeInTheDocument()
    await waitFor(() => expect(within(list).queryByRole('link', { name: 'Hello there' })).toBeNull())

    await userEvent.click(within(list).getByRole('button', { name: 'Archived chats' }))
    await userEvent.click(await within(list).findByRole('button', { name: 'Actions for Hello there' }))
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Unarchive' }))
    await waitFor(() => expect(calls.filter((c) => c.method === 'PATCH').at(-1)?.body).toEqual({ archived: false }))
    await userEvent.click(within(list).getByRole('button', { name: 'All chats' }))

    await userEvent.click(await within(list).findByRole('button', { name: 'Actions for Hello there' }))
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Fork' }))
    await waitFor(() => expect(router.state.location.pathname).toBe('/chat/c2'))
    const note = (await screen.findByText(/Forked from/)).closest('p')!
    expect(within(note).getByRole('link', { name: 'Hello there' })).toHaveAttribute('href', '/chat/c1')
  })

  it('a question just sent is the one the rail marks', async () => {
    const two = [...answered, msg('q2', 'a1', 'user', { content: 'Second?' }), msg('a2', 'q2', 'assistant', { content: 'Second answer.', model: 'Main-Model' })]
    const three = [...two, msg('q3', 'a2', 'user', { content: 'Third?' }), msg('a3', 'q3', 'assistant', { content: 'Third answer.', model: 'Main-Model' })]
    backend({
      start: conversation({ messages: two, currentLeafId: 'a2', title: 'Hello there' }),
      events: [
        { type: 'question', id: 'q3', parentId: 'a2' },
        { type: 'assistant', id: 'a3', parentId: 'q3', model: 'Main-Model' },
        { type: 'content', text: 'Third answer.' },
        { type: 'done', id: 'a3' },
      ],
      saved: three,
    })
    renderApp('/chat/c1')
    const rail = await screen.findByRole('navigation', { name: 'Questions in this chat' })
    await waitFor(() => expect(within(rail).getAllByRole('button')[1]).toHaveAttribute('aria-current', 'location'))
    await ask('Third?')
    await waitFor(() => expect(within(rail).getAllByRole('button')).toHaveLength(3))
    await waitFor(() => expect(within(rail).getAllByRole('button')[2]).toHaveAttribute('aria-current', 'location'))
    expect(within(rail).getAllByRole('button')[1]).not.toHaveAttribute('aria-current')
  })

  it('an answer forks a new chat from there, the rail jumps between questions, and an archived chat says so', async () => {
    const two = [
      ...answered,
      msg('q2', 'a1', 'user', { content: 'And what about the second thing?' }),
      msg('a2', 'q2', 'assistant', { content: 'The second answer.', model: 'Main-Model' }),
    ]
    const calls = backend({
      start: conversation({ messages: two, currentLeafId: 'a2', title: 'Hello there', archivedAt: new Date().toISOString() }),
      extra: {
        'PATCH /api/chat/conversations/c1': () => ({ status: 204 }),
        'POST /api/chat/conversations/c1/fork': () => ({ status: 201, json: { id: 'c3', title: 'Hello there (fork)' } }),
        'GET /api/chat/conversations/c3': () => ({ json: conversation({ id: 'c3', messages: answered, currentLeafId: 'a1' }) }),
      },
    })
    const { router } = renderApp('/chat/c1')
    expect(await screen.findByText(/This chat is archived/)).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Unarchive' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'PATCH')?.body).toEqual({ archived: false }))

    const rail = screen.getByRole('navigation', { name: 'Questions in this chat' })
    const marks = within(rail).getAllByRole('button')
    expect(marks.map((b) => b.getAttribute('aria-label'))).toEqual(['Question 1: hello there', 'Question 2: And what about the second thing?'])
    const scrolled = vi.spyOn(Element.prototype, 'scrollIntoView')
    await userEvent.click(marks[1]!)
    expect(scrolled.mock.contexts[0]).toHaveAttribute('data-question', 'q2')
    expect(marks[1]).toHaveAttribute('aria-current', 'location')
    expect(document.activeElement).toHaveAttribute('data-question', 'q2')

    const first = screen.getAllByRole('region', { name: 'Answer' })[0]!
    await userEvent.click(within(first).getByRole('button', { name: 'Fork from here' }))
    await waitFor(() => expect(calls.find((c) => c.path.endsWith('/fork'))?.body).toEqual({ messageId: 'a1' }))
    await waitFor(() => expect(router.state.location.pathname).toBe('/chat/c3'))
  })

  it("a chat's tools are chosen in the composer, before and after the chat exists", async () => {
    const calls = backend({ events: answer, saved: answered, extra: { 'PATCH /api/chat/conversations/c1': () => ({ status: 204 }) } })
    renderApp('/chat')
    await userEvent.click(await screen.findByRole('button', { name: 'Tools: 2 of 2 on' }))
    const argus = await screen.findByRole('switch', { name: /Argus/ })
    expect(screen.getByText('Asks you before each call')).toBeInTheDocument()
    await userEvent.click(argus)
    expect(screen.getByRole('button', { name: 'Tools: 1 of 2 on' })).toBeInTheDocument()
    await userEvent.keyboard('{Escape}')
    await ask('hello there')
    await waitFor(() => expect(calls.find((c) => c.method === 'POST' && c.path === '/api/chat/conversations')?.body).toEqual({ tools: ['calculator'] }))
    // In a saved chat (once the thread, and its own composer, is on screen), a change is saved at once.
    expect(await within(await screen.findByRole('region', { name: 'Answer' })).findByText(/Here is code/)).toBeInTheDocument()
    await userEvent.click(await screen.findByRole('button', { name: /^Tools:/ }))
    await userEvent.click(await screen.findByRole('switch', { name: /Calculator/ }))
    await waitFor(() => expect(calls.find((c) => c.method === 'PATCH')?.body).toEqual({ tools: ['argus'] }))
  })

  it('a tool that asks first waits for Allow, and a picture a tool made is shown', async () => {
    const calls = backend({
      events: [
        { type: 'question', id: 'q1', parentId: null },
        { type: 'assistant', id: 'a1', parentId: 'q1', model: 'Main-Model' },
        { type: 'tool_call', id: 'call_1', name: 'calculate', arguments: '{"expression":"6*7"}', tool: 'calculator' },
        { type: 'approval', id: 'call_1', name: 'calculate', arguments: '{"expression":"6*7"}', tool: 'calculator', title: 'Calculator' },
      ],
      hang: true,
      extra: { 'POST /api/chat/conversations/c1/tool-calls/call_1': () => ({ status: 204 }) },
    })
    renderApp('/chat')
    await ask('what is 6*7')
    const a = await screen.findByRole('region', { name: 'Answer' })
    expect(await within(a).findByText('Waiting for you')).toBeInTheDocument()
    expect(within(a).getByRole('alert')).toHaveTextContent('Allow Calculate to run with these arguments?')
    // Open, so the person reads what it would run before allowing it.
    expect(within(a).getByText('Asked with')).toBeInTheDocument()
    await userEvent.click(within(a).getByRole('button', { name: 'Allow' }))
    await waitFor(() => expect(calls.find((c) => c.path.endsWith('/tool-calls/call_1'))?.body).toEqual({ allow: true }))
    await waitFor(() => expect(within(a).queryByText('Waiting for you')).toBeNull())
  })

  it('a picture made by the image tool shows in the answer, opens full size, and is in the Files panel', async () => {
    const picture = { id: 'img1', fileName: 'a-red-fox.png', size: 4096, truncated: false, kind: 'image' as const, contentType: 'image/png' }
    const messages = [
      msg('q1', null, 'user', { content: 'draw a fox' }),
      msg('a1', 'q1', 'assistant', { toolCalls: [{ id: 'c1', function: { name: 'generate_image', arguments: '{"prompt":"a red fox"}' } }] }),
      msg('t1', 'a1', 'tool', { toolCallId: 'c1', toolName: 'generate_image', content: '{"shown_to_the_person":true}', attachments: [picture] }),
      msg('a2', 't1', 'assistant', { content: 'Here is a red fox.' }),
    ]
    backend({ start: conversation({ messages, currentLeafId: 'a2' }) })
    renderApp('/chat/c1')
    const a = await screen.findByRole('region', { name: 'Answer' })
    await userEvent.click(within(within(a).getByRole('list', { name: 'Pictures' })).getByRole('button', { name: 'View a-red-fox.png' }))
    expect(await screen.findByRole('dialog', { name: 'a-red-fox.png' })).toBeInTheDocument()
    await userEvent.keyboard('{Escape}')
    await userEvent.click(screen.getByRole('button', { name: /^Files \(1\)$/, hidden: true }))
    const panel = await screen.findByRole('complementary', { name: 'Files' })
    expect(within(panel).getByText(/made in this chat/)).toBeInTheDocument()
  })

  it('a page a Python run wrote opens running, from its own bytes', async () => {
    const chart = { id: 'f3', fileName: 'chart.html', size: 5_000_000, truncated: true, kind: 'text' as const, contentType: 'text/plain', original: true }
    const messages = [
      msg('q1', null, 'user', { content: 'chart it' }),
      msg('a1', 'q1', 'assistant', { toolCalls: [{ id: 'c1', function: { name: 'run_python', arguments: '{"code":"fig.write_html()"}' } }] }),
      msg('t1', 'a1', 'tool', { toolCallId: 'c1', toolName: 'run_python', content: '{"exit_code":0}', attachments: [chart] }),
      msg('a2', 't1', 'assistant', { content: 'Here is the chart.' }),
    ]
    const asked: string[] = []
    backend({
      start: conversation({ messages, currentLeafId: 'a2' }),
      extra: { 'GET /api/chat/attachments/f3/content': (_body, _init, url) => (asked.push(url.search), { json: '<html><body>whole page</body></html>' }) },
    })
    renderApp('/chat/c1')
    const made = within(await screen.findByRole('region', { name: 'Answer' })).getByRole('list', { name: 'Files made' })
    await userEvent.click(within(made).getByRole('button', { name: 'Open chart.html in the Files panel' }))
    const panel = await screen.findByRole('complementary', { name: 'Files' })
    expect(await within(panel).findByTitle('Preview of chart.html')).toHaveAttribute('src', '/preview.html')
    // The whole file, not the text the model read (cut at a million characters).
    expect(asked.some((u) => u.includes('download'))).toBe(true)
    await userEvent.click(within(panel).getByRole('tab', { name: 'Code' }))
    expect(await within(panel).findByText(/whole page/)).toBeInTheDocument()
  })

  it('files a Python run wrote: text opens in the Files panel, anything else is there to download', async () => {
    const csv = { id: 'f1', fileName: 'summary.csv', size: 20, truncated: false, kind: 'text' as const, contentType: 'text/plain' }
    const zip = { id: 'f2', fileName: 'export.zip', size: 2048, truncated: false, kind: 'file' as const, contentType: 'application/octet-stream', original: true }
    const messages = [
      msg('q1', null, 'user', { content: 'summarise' }),
      msg('a1', 'q1', 'assistant', { toolCalls: [{ id: 'c1', function: { name: 'run_python', arguments: '{"code":"print(1)"}' } }] }),
      msg('t1', 'a1', 'tool', { toolCallId: 'c1', toolName: 'run_python', content: '{"exit_code":0,"stdout":"1\\n"}', attachments: [csv, zip] }),
      msg('a2', 't1', 'assistant', { content: 'Done.' }),
    ]
    backend({ start: conversation({ messages, currentLeafId: 'a2' }), extra: { 'GET /api/chat/attachments/f1/content': () => ({ json: 'month,total\n1,42' }) } })
    renderApp('/chat/c1')
    const made = within(await screen.findByRole('region', { name: 'Answer' })).getByRole('list', { name: 'Files made' })
    expect(within(made).getByRole('link', { name: 'Download export.zip' })).toHaveAttribute('href', '/api/chat/attachments/f2/content?download=1')
    await userEvent.click(within(made).getByRole('button', { name: 'Open summary.csv in the Files panel' }))
    const panel = await screen.findByRole('complementary', { name: 'Files' })
    expect(await within(panel).findByText(/month,total/)).toBeInTheDocument()
    await userEvent.click(within(panel).getByRole('button', { name: /Back|All files/ }))
    await userEvent.click(within(panel).getByRole('button', { name: /export\.zip/ }))
    expect(within(panel).getByText(/not a file the page can show/)).toBeInTheDocument()
    expect(within(panel).getByRole('link', { name: /Download/ })).toHaveAttribute('download', 'export.zip')
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

  it('a chat still answering (its page was closed) is watched again from the start, and Stop asks the server', async () => {
    const question = msg('q1', null, 'user', { content: 'long one' })
    const calls = backend({
      start: conversation({ messages: [question], currentLeafId: 'q1', answering: true }),
      saved: [question, msg('a1', 'q1', 'assistant', { content: 'written while away', status: 'stopped' })],
      extra: {
        'GET /api/chat/conversations/c1/stream': () => ({
          events: [{ type: 'question', id: 'q1', parentId: null }, { type: 'assistant', id: 'a1', parentId: 'q1', model: 'Main-Model' }, { type: 'content', text: 'written while away' }],
          hang: true,
        }),
      },
    })
    renderApp('/chat/c1')
    expect(await screen.findByText('written while away')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Stop' }))
    expect(await screen.findByText('Stopped.')).toBeInTheDocument()
    expect(calls.filter((c) => c.path === '/api/chat/conversations/c1/stream')).toHaveLength(1)
    expect(calls.some((c) => c.method === 'POST' && c.path === '/api/chat/conversations/c1/stop')).toBe(true)
  })

  it('/compact summarizes the chat; the page marks where, and shows the summary on demand', async () => {
    const summary = 'The person said hello; the assistant wrote hello.py.'
    let compacted = false
    const calls = backend({
      extra: {
        'GET /api/chat/conversations/c1': () => ({
          json: conversation({ messages: compacted ? [answered[0]!, { ...answered[1]!, summary }] : answered, currentLeafId: 'a1' }),
        }),
        'POST /api/chat/conversations/c1/compact': () => {
          compacted = true
          return { events: [{ type: 'compacting' }, { type: 'compacted', id: 'a1', summary, auto: false, covered: 2 }, { type: 'done', id: 'a1' }] }
        },
      },
    })
    renderApp('/chat/c1')
    await screen.findByText('Hi! Here is code:')
    await ask('/compact')
    expect(await screen.findByText('Compacted: 2 messages summarized. The next answers read the summary.')).toBeInTheDocument()
    expect(calls.some((c) => c.method === 'POST' && c.path === '/api/chat/conversations/c1/compact')).toBe(true)
    expect(calls.some((c) => c.path === '/api/chat/conversations/c1/messages')).toBe(false)
    const mark = await screen.findByRole('note', { name: 'Chat compacted' })
    await userEvent.click(within(mark).getByRole('button', { name: /Show summary/ }))
    expect(within(mark).getByText(/the assistant wrote hello\.py/)).toBeInTheDocument()
  })

  it('the context gauge shows how full, what fills it, and compacts from there', async () => {
    const context = { system: 4000, instructions: 0, tools: 6000, summary: 0, files: 8000, you: 1000, answers: 1000, toolResults: 0 }
    const full = [answered[0]!, { ...answered[1]!, promptTokens: 20_000, completionTokens: 200, context }]
    const calls = backend({ start: conversation({ messages: full, currentLeafId: 'a1' }), extra: { 'POST /api/chat/conversations/c1/compact': () => ({ events: [{ type: 'done', id: 'a1' }] }) } })
    renderApp('/chat/c1')
    await userEvent.click(await screen.findByRole('button', { name: 'Context: 62% full. Details and compact' }))
    const parts = await screen.findByRole('list', { name: 'What fills it' })
    // 20,000 prompt tokens by the characters of each kind (20,000 in all); the answer's 200 count as answers.
    expect(within(parts).getByText('Files').parentElement).toHaveTextContent('Files8 K24%')
    expect(within(parts).getByText('Tool definitions').parentElement).toHaveTextContent('Tool definitions6 K18%')
    expect(within(parts).getByText('Answers').parentElement).toHaveTextContent('Answers1.2 K4%')
    // Kept for the answer: the model's longest (8,192); the rest is free.
    expect(within(parts).getByText('Free').parentElement).toHaveTextContent('Free4.38 K13%')
    await userEvent.click(screen.getByRole('button', { name: 'Compact now' }))
    await waitFor(() => expect(calls.some((c) => c.method === 'POST' && c.path === '/api/chat/conversations/c1/compact')).toBe(true))
  })

  it('after compacting, the context gauge shows what the next request carries, worked out', async () => {
    const context = { system: 4000, instructions: 0, tools: 6000, summary: 0, files: 8000, you: 1000, answers: 1000, toolResults: 0 }
    const full = [answered[0]!, { ...answered[1]!, promptTokens: 20_000, completionTokens: 200, context, summary: 'x'.repeat(1850) }]
    backend({ start: conversation({ messages: full, currentLeafId: 'a1' }) })
    renderApp('/chat/c1')
    // 4,000 + 6,000 + 2,000 (the summary and its frame) characters, at one token each: 12,000 of 32,768.
    await userEvent.click(await screen.findByRole('button', { name: 'Context: 37% full. Details and compact' }))
    expect(await screen.findByText(/About 12 K of 32.77 K tokens \(37%\) since compacting/)).toBeInTheDocument()
    const parts = screen.getByRole('list', { name: 'What fills it' })
    expect(within(parts).queryByText('Files')).not.toBeInTheDocument()
    expect(within(parts).getByText('Summary of earlier messages')).toBeInTheDocument()
  })

  it('a tool call shows its code and its output as code blocks', async () => {
    const messages = [
      msg('q1', null, 'user', { content: 'count files' }),
      msg('a1', 'q1', 'assistant', { toolCalls: [{ id: 'c1', function: { name: 'run_python', arguments: JSON.stringify({ code: 'import os\nprint(len(os.listdir()))' }) } }] }),
      msg('t1', 'a1', 'tool', { toolCallId: 'c1', toolName: 'run_python', content: '{"exit_code":1,"stdout":"3\\n","stderr":"Traceback: boom","seconds":0.2}', status: 'failed' }),
      msg('a2', 't1', 'assistant', { content: 'Three files.' }),
    ]
    backend({ start: conversation({ messages, currentLeafId: 'a2' }) })
    renderApp('/chat/c1')
    await userEvent.click(await screen.findByRole('button', { name: /Run python/ }))
    const code = screen.getByRole('figure', { name: 'Code: code · python' })
    expect(within(code).getByRole('button', { name: 'Copy code' })).toBeInTheDocument()
    expect(code.textContent).toContain('print(len(os.listdir()))')
    expect(within(screen.getByRole('figure', { name: 'Code: Output' })).getByText('3')).toBeInTheDocument()
    expect(screen.getByRole('figure', { name: 'Code: Error' }).textContent).toContain('Traceback: boom')
    expect(screen.getByText('Exit code 1')).toBeInTheDocument()
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

  it('answering again right after a stop keeps the new answer on screen as it streams', async () => {
    const stoppedTry = msg('a2', 'q1', 'assistant', { content: 'first try', status: 'stopped' })
    let tries = 0
    let reads = 0
    fakeApi(member, {
      'GET /api/chat/config': () => ({ json: config }),
      'GET /api/chat/conversations': () => ({ json: [] }),
      // The stopped answer is saved a moment after the stop: the page waits for it.
      'GET /api/chat/conversations/c1': () => ({
        json: conversation({ messages: tries > 0 && ++reads > 2 ? [...answered, stoppedTry] : answered, currentLeafId: tries > 0 && reads > 2 ? 'a2' : 'a1' }),
      }),
      'POST /api/chat/conversations/c1/regenerate': () => {
        tries++
        const id = tries === 1 ? 'a2' : 'a3'
        return {
          events: [{ type: 'question', id: 'q1', parentId: null }, { type: 'assistant', id, parentId: 'q1', model: 'Main-Model' }, { type: 'content', text: tries === 1 ? 'first try' : 'fresh answer' }],
          hang: true,
        }
      },
    })
    renderApp('/chat/c1')
    const again = async () => {
      await userEvent.click(await screen.findByRole('button', { name: 'Answer again' }))
      await userEvent.click(await screen.findByRole('menuitem', { name: 'Answer again' }))
    }
    await again()
    expect(await screen.findByText('first try')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Stop' }))
    await again()
    expect(await screen.findByText('fresh answer')).toBeInTheDocument()
    // The stopped run would have found its saved answer by now, and cleared the screen.
    await new Promise((r) => setTimeout(r, 1500))
    expect(screen.getByText('fresh answer')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Stop' })).toBeInTheDocument()
  })

  it('answering again can use another thinking level', async () => {
    const calls = backend({ start: conversation({ messages: answered, currentLeafId: 'a1' }), events: [{ type: 'done', id: 'x' }], saved: answered })
    renderApp('/chat/c1')
    await userEvent.click(await screen.findByRole('button', { name: 'Answer again' }))
    await userEvent.click(await screen.findByRole('menuitem', { name: 'No thinking' }))
    await waitFor(() => expect(calls.find((c) => c.path === '/api/chat/conversations/c1/regenerate')?.body).toEqual({ messageId: 'q1', thinking: 'off' }))
  })

  it('an answer comes again shorter or longer than the one on screen', async () => {
    const calls = backend({ start: conversation({ messages: answered, currentLeafId: 'a1' }), events: [{ type: 'done', id: 'x' }], saved: answered })
    renderApp('/chat/c1')
    await userEvent.click(await screen.findByRole('button', { name: 'Answer again' }))
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Shorter' }))
    await waitFor(() => expect(calls.find((c) => c.path === '/api/chat/conversations/c1/regenerate')?.body).toEqual({ messageId: 'q1', length: 'shorter', answerId: 'a1' }))
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

  it('answer now: while the model thinks, a button asks it to stop thinking and answer', async () => {
    const calls = backend({
      events: [{ type: 'question', id: 'q1', parentId: null }, { type: 'assistant', id: 'a1', parentId: 'q1', model: 'Main-Model' }, { type: 'reasoning', text: 'Let me think this through…' }],
      hang: true,
      extra: { 'POST /api/chat/conversations/c1/hurry': () => ({ status: 202 }) },
    })
    renderApp('/chat')
    await ask('a hard one')
    const a = await screen.findByRole('region', { name: 'Answer' })
    await userEvent.click(await within(a).findByRole('button', { name: 'Answer now' }))
    await waitFor(() => expect(calls.some((c) => c.method === 'POST' && c.path === '/api/chat/conversations/c1/hurry')).toBe(true))
    expect(within(a).getByRole('button', { name: 'Answering now…' })).toBeDisabled()
  })

  it('a message written while the answer runs is queued, and Send now stops the answer and sends it', async () => {
    const calls = backend({
      events: [{ type: 'question', id: 'q1', parentId: null }, { type: 'assistant', id: 'a1', parentId: 'q1', model: 'Main-Model' }, { type: 'content', text: 'Working on it' }],
      hang: true,
      saved: [msg('q1', null, 'user', { content: 'first' }), msg('a1', 'q1', 'assistant', { content: 'Working on it', status: 'stopped' })],
    })
    renderApp('/chat')
    await ask('first')
    await screen.findByRole('button', { name: 'Stop' })
    await ask('second')
    const queued = await screen.findByRole('list', { name: 'Queued messages' })
    expect(queued).toHaveTextContent('second')
    expect(calls.filter((c) => c.path === '/api/chat/conversations/c1/messages')).toHaveLength(1)
    await userEvent.click(within(queued).getByRole('button', { name: 'Send now' }))
    await waitFor(() => expect(calls.some((c) => c.path === '/api/chat/conversations/c1/stop')).toBe(true))
    await waitFor(() => expect(calls.filter((c) => c.path === '/api/chat/conversations/c1/messages').map((c) => (c.body as { content: string }).content)).toEqual(['first', 'second']), { timeout: 4000 })
    expect(screen.queryByRole('list', { name: 'Queued messages' })).not.toBeInTheDocument()
  })

  it('deep research goes with the message written with it on, then turns off', async () => {
    const calls = backend({ events: answer, saved: answered })
    renderApp('/chat')
    const toggle = await screen.findByRole('button', { name: 'Deep research' })
    await userEvent.click(toggle)
    expect(toggle).toHaveAttribute('aria-pressed', 'true')
    expect(screen.getByRole('textbox', { name: 'Message' })).toHaveAttribute('placeholder', 'What should be researched?')
    await ask('Compare the codecs')
    await waitFor(() => expect(calls.find((c) => c.path === '/api/chat/conversations/c1/messages')?.body).toMatchObject({ content: 'Compare the codecs', research: true }))
    expect(await screen.findByRole('button', { name: 'Deep research' })).toHaveAttribute('aria-pressed', 'false')
  })

  it('deep research says which step it is on, and the sub-agents card how many parts are done', async () => {
    const parts = JSON.stringify({ tasks: [{ title: 'Speed', instructions: 'Find the speeds.' }, { title: 'Cost', instructions: 'Find the costs.' }] })
    backend({
      events: [
        { type: 'question', id: 'q1', parentId: null },
        { type: 'research' },
        { type: 'assistant', id: 'a1', parentId: 'q1', model: 'Main-Model' },
        { type: 'reasoning', text: 'Two questions.' },
        { type: 'tool_call', id: 'd1', name: 'delegate', arguments: parts },
        { type: 'agent', id: 'd1', index: 0, event: 'start', title: 'Speed', instructions: 'Find the speeds.' },
        { type: 'agent', id: 'd1', index: 1, event: 'start', title: 'Cost', instructions: 'Find the costs.' },
        { type: 'agent', id: 'd1', index: 0, event: 'done', ms: 1000 },
      ],
      hang: true,
    })
    renderApp('/chat')
    await userEvent.click(await screen.findByRole('button', { name: 'Deep research' }))
    await ask('Compare the codecs')
    const a = await screen.findByRole('region', { name: 'Answer' })
    expect(await within(a).findByText('Deep research: Researching 2 parts: 1 of 2 parts done…')).toBeInTheDocument()
    // The card says it too, in place of "Running", and above its parts.
    expect(within(a).getAllByText(/^1 of 2 parts done/).length).toBe(2)
  })

  it('an admin opens an answer’s trace from the answer; others have no such button', async () => {
    const trace: AnswerTrace = {
      id: 'a1', conversationId: 'c1', at: '2026-10-04T10:00:00Z', model: 'Main-Model', status: 'complete', ms: 5000, queueMs: 0, setupMs: 100, rounds: 1, toolCalls: 0, agents: 0,
      tokens: { prompt: 1000, cached: 400, completion: 200, cacheShare: 0.4 }, agentTokens: { prompt: 0, cached: 0, completion: 0, cacheShare: null }, prompt: [], person: null,
      slowest: { kind: 'round', label: 'Model, round 1', ms: 4900, share: 0.98, detail: 'Mostly writing: 200 tokens, at 41 a second.' },
      steps: [
        {
          kind: 'round', label: 'Model, round 1', ms: 4900, slowest: true, index: 1, status: 'complete', thinkingMs: 2300, firstTokenMs: 300, tokens: { prompt: 1000, cached: 400, completion: 200, cacheShare: 0.4 },
          readPerSecond: 2000, writePerSecond: 41, speedFrom: 'clock', name: null, resultChars: null, files: null, agents: null,
        },
      ],
    }
    const calls = backend({ me: admin, start: conversation({ messages: answered, currentLeafId: 'a1' }), extra: { 'GET /api/admin/traces/a1': () => ({ json: trace }) } })
    const first = renderApp('/chat/c1')
    const a = await screen.findByRole('region', { name: 'Answer' })
    await userEvent.click(within(a).getByRole('button', { name: 'Answer trace' }))
    const panel = await screen.findByRole('dialog', { name: 'Answer trace' })
    expect(await within(panel).findByRole('region', { name: 'Slowest step' })).toHaveTextContent('Slowest step: Model, round 1 · 4.9 s (98% of the answer)')
    expect(calls.some((c) => c.path === '/api/admin/traces/a1')).toBe(true)
    first.unmount()

    backend({ start: conversation({ messages: answered, currentLeafId: 'a1' }) })
    renderApp('/chat/c1')
    const theirs = await screen.findByRole('region', { name: 'Answer' })
    expect(within(theirs).getByRole('button', { name: 'Copy answer' })).toBeInTheDocument()
    expect(within(theirs).queryByRole('button', { name: 'Answer trace' })).not.toBeInTheDocument()
  })

  it('a sent file leaves the box as soon as the question is taken, while the answer still streams', async () => {
    const calls = backend({ events: [{ type: 'question', id: 'q1', parentId: null }, { type: 'assistant', id: 'a1', parentId: 'q1', model: 'Main-Model' }], hang: true })
    renderApp('/chat')
    await userEvent.upload(await screen.findByLabelText('Attach files'), new File(['a,b'], 'data.csv', { type: 'text/csv' }))
    expect(await screen.findByRole('list', { name: 'Files to send' })).toHaveTextContent('data.csv')
    await ask('sum it')
    await waitFor(() => expect(calls.find((c) => c.path === '/api/chat/conversations/c1/messages')?.body).toMatchObject({ attachments: ['att-data.csv'] }))
    await waitFor(() => expect(screen.queryByRole('list', { name: 'Files to send' })).not.toBeInTheDocument())
    expect(screen.getByRole('button', { name: 'Stop' })).toBeInTheDocument()
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
