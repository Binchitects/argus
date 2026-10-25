import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { admin, fakeApi, member, renderApp } from '@/test/utils'
import type { MemoryItem, MemoryView } from '@/lib/memory'
import type { ChatConfig, Conversation } from './chat/types'
import { blank } from './chat/live'

const item = (over: Partial<MemoryItem>): MemoryItem => ({
  id: 'm1', text: '', scope: 'person', kind: 'fact', project: null, source: 'typed', sourceId: null, pinned: false, usedAt: null, uses: 0, replacedBy: null, proposed: false, replaces: null,
  createdAt: '2026-10-01T10:00:00Z', updatedAt: '2026-10-01T10:00:00Z', mine: true, canEdit: true, ...over,
})

const view = (over: Partial<MemoryView> = {}): MemoryView => ({
  enabled: true, on: true, auto: false, max: 1000, maxChars: 500, keepsCompany: false,
  memories: [
    item({ id: 'm1', text: 'Deploys with Podman.', kind: 'howto', source: 'chat', sourceId: 'c7', uses: 3, usedAt: '2026-10-20T10:00:00Z' }),
    item({ id: 'm2', text: 'Builds with tools/dn.', scope: 'project', project: 'gitlab.corp/payments/api', source: 'code-arena' }),
    item({ id: 'm3', text: 'Our fiscal year starts in April.', scope: 'company', pinned: true, mine: false, canEdit: false }),
  ],
  ...over,
})

describe('the memory page', () => {
  it('lists what is remembered with its kind and whose, finds and narrows, and pins and keeps', async () => {
    let shown = view()
    const calls = fakeApi(member, {
      'GET /api/account/memories': () => ({ json: shown }),
      'PUT /api/account/memories/m1': (body) => {
        shown = { ...shown, memories: shown.memories.map((m) => (m.id === 'm1' ? { ...m, ...(body as object) } : m)) }
        return { json: shown.memories[0] }
      },
      'POST /api/account/memories': () => ({ status: 201, json: item({ id: 'm4', text: 'Prefers short answers.', kind: 'preference' }) }),
    })
    renderApp('/memory')
    const list = await screen.findByRole('list', { name: 'Memories' })
    const podman = within(list).getByRole('listitem', { name: 'Deploys with Podman.' })
    expect(within(podman).getByRole('combobox', { name: 'Kind: Deploys with Podman.' })).toHaveValue('howto')
    expect(within(podman).getByRole('link', { name: 'from a chat' })).toHaveAttribute('href', '/chat/c7')
    expect(podman).toHaveTextContent('read 3 times')
    expect(within(list).getByRole('listitem', { name: 'Builds with tools/dn.' })).toHaveTextContent('payments/api')
    // The company's: read by everyone, changed only by its keepers.
    const fiscal = within(list).getByRole('listitem', { name: 'Our fiscal year starts in April.' })
    expect(fiscal).toHaveTextContent("The company's")
    expect(within(fiscal).queryByRole('button', { name: /Forget/ })).not.toBeInTheDocument()

    await userEvent.click(screen.getByRole('radio', { name: "The company's" }))
    expect(within(list).getAllByRole('listitem')).toHaveLength(1)
    await userEvent.click(screen.getByRole('radio', { name: 'All' }))
    await userEvent.type(screen.getByRole('textbox', { name: 'Find a memory' }), 'podman')
    expect(within(list).getAllByRole('listitem')).toHaveLength(1)

    // Shown again after the filters: found again.
    await userEvent.click(within(within(list).getByRole('listitem', { name: 'Deploys with Podman.' })).getByRole('button', { name: 'Pin: Deploys with Podman.' }))
    await waitFor(() => expect(calls.filter((c) => c.method !== 'GET').map((c) => [c.method, c.path, c.body])).toEqual([['PUT', '/api/account/memories/m1', { pinned: true }]]))

    await userEvent.type(screen.getByRole('textbox', { name: 'Something to remember' }), 'Prefers short answers.')
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Its kind' }), 'preference')
    await userEvent.click(screen.getByRole('button', { name: 'Remember' }))
    await waitFor(() =>
      expect(calls.find((c) => c.method === 'POST' && c.path === '/api/account/memories')?.body).toEqual({ text: 'Prefers short answers.', kind: 'preference', scope: 'person', pinned: false }),
    )
    // Only a keeper of the company's knowledge may keep the company's.
    expect(screen.queryByRole('checkbox', { name: /The company's/ })).not.toBeInTheDocument()
  })

  it('a keeper of the company\'s knowledge keeps a memory for everyone', async () => {
    const calls = fakeApi(admin, {
      'GET /api/account/memories': () => ({ json: view({ keepsCompany: true }) }),
      'POST /api/account/memories': () => ({ status: 201, json: item({ id: 'm5', scope: 'company' }) }),
    })
    renderApp('/memory')
    await userEvent.type(await screen.findByRole('textbox', { name: 'Something to remember' }), 'The VPN is vpn.corp.')
    await userEvent.click(screen.getByRole('checkbox', { name: /The company's/ }))
    await userEvent.click(screen.getByRole('button', { name: 'Remember' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'POST')?.body).toMatchObject({ text: 'The VPN is vpn.corp.', scope: 'company' }))
  })
})

describe('memories proposed from quiet chats', () => {
  it('are shown apart to keep or not, and memory can keep them on its own', async () => {
    let shown = view({
      memories: [item({ id: 'p1', text: 'Publishes the docs with Netlify.', kind: 'decision', proposed: true, replaces: 'm9', source: 'reflected', sourceId: 'c3' })],
    })
    const calls = fakeApi(member, {
      'GET /api/account/memories': () => ({ json: shown }),
      'POST /api/account/memories/p1/keep': () => {
        shown = { ...shown, memories: shown.memories.map((m) => ({ ...m, proposed: false })) }
        return { json: shown.memories[0] }
      },
      'PUT /api/account/memories/settings': (body) => ({ json: body }),
    })
    renderApp('/memory')
    const proposed = await screen.findByRole('list', { name: 'Proposed memories' })
    const one = within(proposed).getByRole('listitem', { name: 'Publishes the docs with Netlify.' })
    expect(one).toHaveTextContent('replaces one you kept')
    expect(within(one).getByRole('link', { name: 'from a chat' })).toHaveAttribute('href', '/chat/c3')
    // Not among the memories answers read until kept.
    expect(screen.queryByRole('list', { name: 'Memories' })).not.toBeInTheDocument()
    await userEvent.click(within(one).getByRole('button', { name: 'Keep' }))
    await waitFor(() => expect(calls.some((c) => c.path === '/api/account/memories/p1/keep')).toBe(true))
    expect(await screen.findByRole('list', { name: 'Memories' })).toBeInTheDocument()
    expect(screen.queryByRole('list', { name: 'Proposed memories' })).not.toBeInTheDocument()

    await userEvent.click(screen.getByRole('switch', { name: 'Remember on its own' }))
    await waitFor(() => expect(calls.find((c) => c.path === '/api/account/memories/settings')?.body).toEqual({ on: true, auto: true }))
  })
})

describe('an answer that read memories', () => {
  it('says how many, and shows them on a click', async () => {
    const config: ChatConfig = {
      model: 'Main-Model', models: [{ name: 'Main-Model', context: 32768, maxOutput: 8192, vision: false, tools: true, thinking: false, loaded: true, prices: { input: 0.2, cachedInput: 0.02, output: 0.8 } }],
      presets: [], defaultThinking: null, argus: false, tools: [], gitlabUrl: null, maxUploadBytes: 20 * 1024 * 1024, imageTypes: [],
    }
    const chat: Conversation = {
      id: 'c1', title: 'Deploy', thinking: null, tools: [], useArgus: false, model: null, systemPrompt: null, temperature: null, topP: null, maxTokens: null,
      currentLeafId: 'a1', archivedAt: null, forkedFrom: null, createdAt: '', updatedAt: '',
      messages: [
        { ...blank('q1', 'user', null), content: 'How do I deploy?', status: 'complete' },
        {
          ...blank('a1', 'assistant', 'q1'), content: 'With Podman.', status: 'complete',
          details: { memories: [{ id: 'm1', text: 'Deploys with Podman.', kind: 'howto', scope: 'person' }] },
        },
      ],
    }
    fakeApi(member, {
      'GET /api/chat/config': () => ({ json: config }),
      'GET /api/chat/conversations': () => ({ json: [] }),
      'GET /api/chat/conversations/c1': () => ({ json: chat }),
    })
    renderApp('/chat/c1')
    const note = await screen.findByRole('note', { name: 'Memories it read' })
    await userEvent.click(within(note).getByRole('button', { name: 'Read 1 memory' }))
    expect(within(note).getByText('Deploys with Podman.')).toBeInTheDocument()
    expect(note).toHaveTextContent('(how to)')
    expect(within(note).getByRole('link', { name: 'All memories' })).toHaveAttribute('href', '/memory')
  })
})
