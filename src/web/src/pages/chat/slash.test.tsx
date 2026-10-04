import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import type { PromptItem, PromptLibrary } from '@/lib/prompts'
import { fakeApi, member, renderApp } from '@/test/utils'
import type { ChatConfig, Conversation } from './types'

const config: ChatConfig = {
  model: 'Main-Model',
  models: [{ name: 'Main-Model', context: 32768, maxOutput: 8192, vision: false, tools: true, thinking: true, loaded: true, prices: { input: null, cachedInput: null, output: null } }],
  presets: [],
  defaultThinking: null,
  argus: false,
  tools: [],
  gitlabUrl: null,
  maxUploadBytes: 1024 * 1024,
  imageTypes: [],
}

const conversation: Conversation = {
  id: 'c1', title: 'New chat', thinking: null, tools: [], useArgus: false, model: null, systemPrompt: null, temperature: null, topP: null, maxTokens: null,
  currentLeafId: null, archivedAt: null, forkedFrom: null, createdAt: '', updatedAt: '', messages: [],
}

const prompt = (over: Partial<PromptItem>): PromptItem => ({
  id: over.name ?? 'p', name: 'p', title: 'P', text: '', variables: [], sharing: 'Personal', groups: [], source: 'mine', from: null, canEdit: true, updatedAt: '', ...over,
})

const library: PromptLibrary = {
  isAdmin: false,
  groups: [],
  prompts: [
    prompt({ name: 'review', title: 'Review code', text: 'Review {{file}} for {{focus}}. Be brief about {{file}}.', variables: ['file', 'focus'] }),
    prompt({ name: 'standup', title: 'Stand-up notes', text: 'Write my stand-up notes.', source: 'company', sharing: 'Company' }),
    prompt({ name: 'triage', title: 'Triage a GitLab issue', text: 'Triage #{{issue}}.', variables: ['issue'], source: 'plugin', from: 'GitLab issues', canEdit: false }),
  ],
}

function backend() {
  return fakeApi(member, {
    'GET /api/chat/config': () => ({ json: config }),
    'GET /api/chat/conversations': () => ({ json: [] }),
    'POST /api/chat/conversations': () => ({ status: 201, json: conversation }),
    'GET /api/chat/conversations/c1': () => ({ json: conversation }),
    'POST /api/chat/conversations/c1/messages': () => ({
      events: [
        { type: 'question', id: 'q1', parentId: null },
        { type: 'assistant', id: 'a1', parentId: 'q1', model: 'Main-Model' },
        { type: 'content', text: 'Done.' },
        { type: 'done', id: 'a1' },
      ],
    }),
    'GET /api/prompts': () => ({ json: library }),
  })
}

describe('slash prompts', () => {
  it('/review fills its blanks and sends the prompt filled in', async () => {
    const calls = backend()
    renderApp('/chat')
    const box = await screen.findByRole('textbox', { name: 'Message' })
    await userEvent.type(box, '/rev')
    const menu = await screen.findByRole('listbox', { name: 'Prompts' })
    expect(within(menu).getAllByRole('option').map((o) => o.textContent)).toEqual([expect.stringContaining('/review')])
    await userEvent.keyboard('{Enter}')

    // Its text in the box, a field for each blank, the first one ready to type in.
    expect(box).toHaveValue('Review {{file}} for {{focus}}. Be brief about {{file}}.')
    const file = screen.getByRole('textbox', { name: 'File' })
    await waitFor(() => expect(file).toHaveFocus())
    expect(screen.queryByRole('listbox', { name: 'Prompts' })).toBeNull()

    // Not sent while a blank is empty.
    await userEvent.type(file, 'src/parse.c')
    await userEvent.click(screen.getByRole('button', { name: 'Send' }))
    expect(await screen.findByText('Fill this in to send.')).toBeInTheDocument()
    expect(calls.some((c) => c.path === '/api/chat/conversations/c1/messages')).toBe(false)

    // Enter on the last blank sends it, filled in.
    await userEvent.type(screen.getByRole('textbox', { name: 'Focus' }), 'buffer overflows{Enter}')
    await waitFor(() =>
      expect(calls.find((c) => c.path === '/api/chat/conversations/c1/messages')?.body).toEqual({
        content: 'Review src/parse.c for buffer overflows. Be brief about src/parse.c.',
        attachments: [],
        root: true,
      }),
    )
    await waitFor(() => expect(box).toHaveValue(''))
    expect(screen.queryByRole('textbox', { name: 'File' })).toBeNull()
  })

  it('the menu finds by title too, says where each comes from, and Escape closes it', async () => {
    backend()
    renderApp('/chat')
    const box = await screen.findByRole('textbox', { name: 'Message' })
    await userEvent.type(box, '/')
    const menu = await screen.findByRole('listbox', { name: 'Prompts' })
    expect(within(menu).getAllByRole('option')).toHaveLength(3)
    expect(within(menu).getByRole('option', { name: /triage/ })).toHaveTextContent('GitLab issues')
    await userEvent.type(box, 'gitlab')
    expect(within(menu).getAllByRole('option')).toHaveLength(1)
    await userEvent.keyboard('{Escape}')
    expect(screen.queryByRole('listbox', { name: 'Prompts' })).toBeNull()
    expect(box).toHaveValue('/gitlab')
  })

  it('a prompt without blanks comes into the box as it is, to send or change', async () => {
    const calls = backend()
    renderApp('/chat')
    const box = await screen.findByRole('textbox', { name: 'Message' })
    await userEvent.type(box, '/st')
    await userEvent.click(await screen.findByRole('option', { name: /standup/ }))
    expect(box).toHaveValue('Write my stand-up notes.')
    expect(screen.queryByRole('group', { name: /Fill in/ })).toBeNull()
    await userEvent.type(box, '{Enter}')
    await waitFor(() => expect(calls.find((c) => c.path === '/api/chat/conversations/c1/messages')?.body).toMatchObject({ content: 'Write my stand-up notes.' }))
  })
})
