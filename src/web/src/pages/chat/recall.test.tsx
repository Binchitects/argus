import { fireEvent, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import type { PromptLibrary } from '@/lib/prompts'
import { fakeApi, member, renderApp } from '@/test/utils'
import { blank } from './live'
import { recallList } from './recall'
import type { ChatConfig, Conversation, Message } from './types'

const config: ChatConfig = {
  model: 'Main-Model',
  models: [{ name: 'Main-Model', context: 32768, maxOutput: 8192, vision: false, tools: true, thinking: false, loaded: true, prices: { input: null, cachedInput: null, output: null } }],
  presets: [],
  defaultThinking: null,
  argus: false,
  tools: [],
  gitlabUrl: null,
  maxUploadBytes: 1024 * 1024,
  imageTypes: [],
}

const msg = (id: string, parentId: string | null, role: Message['role'], content: string): Message => ({ ...blank(id, role, parentId), content })

/** A chat of two questions and their answers: the newest question last. */
const conversation: Conversation = {
  id: 'c1', title: 'Two questions', thinking: null, tools: [], useArgus: false, model: null, systemPrompt: null, temperature: null, topP: null, maxTokens: null,
  currentLeafId: 'a2', archivedAt: null, forkedFrom: null, createdAt: '', updatedAt: '',
  messages: [msg('q1', null, 'user', 'first question'), msg('a1', 'q1', 'assistant', 'One.'), msg('q2', 'a1', 'user', 'second question'), msg('a2', 'q2', 'assistant', 'Two.')],
}

const library: PromptLibrary = {
  isAdmin: false,
  groups: [],
  prompts: [{ id: 'p', name: 'review', title: 'Review code', text: 'Review it.', variables: [], sharing: 'Personal', groups: [], source: 'mine', from: null, canEdit: true, updatedAt: '' }],
}

/** The chat, and the person's messages in their other chats (the "first question" again among them: shown once). */
function backend() {
  return fakeApi(member, {
    'GET /api/chat/config': () => ({ json: config }),
    'GET /api/chat/conversations': () => ({ json: [] }),
    'GET /api/chat/conversations/c1': () => ({ json: conversation }),
    'GET /api/chat/history': (_body, _init, url) => ({
      json: url.searchParams.get('before') ? [] : [{ text: 'from another chat', at: '2026-10-08T10:00:00Z' }, { text: 'first question', at: '2026-10-07T10:00:00Z' }, { text: '/standup in a list', at: '2026-10-06T10:00:00Z' }],
    }),
    'POST /api/chat/conversations/c1/messages': () => ({
      events: [
        { type: 'question', id: 'q3', parentId: 'a2' },
        { type: 'assistant', id: 'a3', parentId: 'q3', model: 'Main-Model' },
        { type: 'content', text: 'Again.' },
        { type: 'done', id: 'a3' },
      ],
    }),
    'GET /api/prompts': () => ({ json: library }),
  })
}

async function box() {
  renderApp('/chat/c1')
  await screen.findByText('Two.')
  return screen.getByRole('textbox', { name: 'Message' }) as HTMLTextAreaElement
}

describe('↑ in the message box', () => {
  it('brings back this chat’s messages, newest first, then the person’s others, and ↓ comes back to the draft, kept', async () => {
    const calls = backend()
    const el = await box()
    await userEvent.type(el, 'half written')
    await userEvent.keyboard('{ArrowUp}')
    expect(el).toHaveValue('second question')
    // The caret at its end, ready to edit.
    expect(el.selectionStart).toBe('second question'.length)
    await userEvent.keyboard('{ArrowUp}')
    expect(el).toHaveValue('first question')
    // This chat has none left: the person's other chats, asked for only now ("first question" is not shown twice).
    expect(calls.some((c) => c.path.startsWith('/api/chat/history'))).toBe(false)
    await userEvent.keyboard('{ArrowUp}')
    await waitFor(() => expect(el).toHaveValue('from another chat'))
    expect(calls.find((c) => c.path.startsWith('/api/chat/history'))?.path).toBe('/api/chat/history?limit=50')
    await userEvent.keyboard('{ArrowUp}')
    expect(el).toHaveValue('/standup in a list')
    // A message brought back that starts with / does not open the prompts menu.
    expect(screen.queryByRole('listbox', { name: 'Prompts' })).toBeNull()
    // The oldest: Up stays there.
    await userEvent.keyboard('{ArrowUp}')
    expect(el).toHaveValue('/standup in a list')

    await userEvent.keyboard('{ArrowDown}{ArrowDown}')
    expect(el).toHaveValue('first question')
    await userEvent.keyboard('{ArrowDown}{ArrowDown}')
    expect(el).toHaveValue('half written')
    // Down at the draft is the box's own.
    await userEvent.keyboard('{ArrowDown}')
    expect(el).toHaveValue('half written')
  })

  it('Esc goes back to the draft, and Enter sends a message brought back (edited, a copy) as a new one', async () => {
    const calls = backend()
    const el = await box()
    await userEvent.type(el, 'draft')
    await userEvent.keyboard('{ArrowUp}{ArrowUp}{Escape}')
    expect(el).toHaveValue('draft')

    await userEvent.clear(el)
    await userEvent.keyboard('{ArrowUp}')
    await userEvent.type(el, ' again')
    await userEvent.keyboard('{Enter}')
    await waitFor(() => expect(calls.find((c) => c.method === 'POST' && c.path === '/api/chat/conversations/c1/messages')?.body).toMatchObject({ content: 'second question again' }))
    expect(el).toHaveValue('')
    // The question it came from is as it was.
    expect(screen.getAllByText('second question').length).toBeGreaterThan(0)
  })

  it('asks again for the person’s other messages once one is sent, so the next chat’s ↑ has it', async () => {
    const calls = backend()
    const el = await box()
    const asked = () => calls.filter((c) => c.path.startsWith('/api/chat/history')).length
    await userEvent.keyboard('{ArrowUp}{ArrowUp}{ArrowUp}')
    await waitFor(() => expect(el).toHaveValue('from another chat'))
    expect(asked()).toBe(1)
    await userEvent.keyboard('{Enter}')
    await waitFor(() => expect(calls.find((c) => c.method === 'POST')?.body).toMatchObject({ content: 'from another chat' }))
    await waitFor(() => expect(asked()).toBe(2))
  })

  it('leaves the arrows to the prompts menu while it is open', async () => {
    backend()
    const el = await box()
    await userEvent.type(el, '/rev')
    expect(await screen.findByRole('listbox', { name: 'Prompts' })).toBeInTheDocument()
    await userEvent.keyboard('{ArrowUp}{ArrowDown}')
    expect(el).toHaveValue('/rev')
    // Closed with Esc, the arrows are the history's again.
    await userEvent.keyboard('{Escape}')
    expect(screen.queryByRole('listbox', { name: 'Prompts' })).toBeNull()
    await userEvent.keyboard('{ArrowUp}')
    expect(el).toHaveValue('second question')
    await userEvent.keyboard('{ArrowDown}')
    expect(el).toHaveValue('/rev')
    expect(screen.queryByRole('listbox', { name: 'Prompts' })).toBeNull()
  })

  it('in several lines, Up and Down move between them until the first or last line', async () => {
    backend()
    const el = await box()
    await userEvent.type(el, 'line one{Shift>}{Enter}{/Shift}line two')
    expect(el).toHaveValue('line one\nline two')
    // The caret on the second line: Up is the box's own.
    await userEvent.keyboard('{ArrowUp}')
    expect(el).toHaveValue('line one\nline two')
    // On the first line, Up brings back the last message; Down returns to the two lines.
    el.setSelectionRange(3, 3)
    await userEvent.keyboard('{ArrowUp}')
    expect(el).toHaveValue('second question')
    await userEvent.keyboard('{ArrowDown}')
    expect(el).toHaveValue('line one\nline two')
    // Down with nothing brought back is the box's own, on any line.
    el.setSelectionRange(0, 0)
    fireEvent.keyDown(el, { key: 'ArrowDown' })
    expect(el).toHaveValue('line one\nline two')
  })

  it('a message of several lines brought back: Up and Down move between its lines first, then step on', async () => {
    const [q1, a1, , a2] = conversation.messages
    const many: Conversation = { ...conversation, messages: [q1!, a1!, msg('q2', 'a1', 'user', 'top line\nbottom line'), a2!] }
    fakeApi(member, {
      'GET /api/chat/config': () => ({ json: config }),
      'GET /api/chat/conversations': () => ({ json: [] }),
      'GET /api/chat/conversations/c1': () => ({ json: many }),
      'GET /api/chat/history': () => ({ json: [] }),
    })
    const el = await box()
    await userEvent.keyboard('{ArrowUp}')
    expect(el).toHaveValue('top line\nbottom line')
    expect(el.selectionStart).toBe(20)
    // The caret at the end of its last line: Up is the box's own (the browser moves the caret to the line above).
    expect(fireEvent.keyDown(el, { key: 'ArrowUp' })).toBe(true)
    expect(el).toHaveValue('top line\nbottom line')
    // On its first line, Up brings back the one before.
    el.setSelectionRange(2, 2)
    expect(fireEvent.keyDown(el, { key: 'ArrowUp' })).toBe(false)
    expect(el).toHaveValue('first question')
    // Down comes back to it, the caret at its end; on its first line Down is the box's own, on its last it goes on to the draft.
    await userEvent.keyboard('{ArrowDown}')
    expect(el).toHaveValue('top line\nbottom line')
    el.setSelectionRange(2, 2)
    expect(fireEvent.keyDown(el, { key: 'ArrowDown' })).toBe(true)
    expect(el).toHaveValue('top line\nbottom line')
    el.setSelectionRange(15, 15)
    expect(fireEvent.keyDown(el, { key: 'ArrowDown' })).toBe(false)
    expect(el).toHaveValue('')
  })

  it('a message of one line brought back steps on at once, even where the box wraps it', async () => {
    backend()
    const el = await box()
    await userEvent.keyboard('{ArrowUp}')
    expect(el).toHaveValue('second question')
    // Drawn in two rows (a narrow box): measured, the caret and the end are on the second, below the start.
    const rows = vi.spyOn(HTMLElement.prototype, 'offsetTop', 'get').mockImplementation(function (this: HTMLElement) {
      return this.tagName === 'SPAN' && this !== this.parentElement?.firstElementChild ? 20 : 0
    })
    try {
      // The caret at its end, on the second row: the next Up still steps on.
      await userEvent.keyboard('{ArrowUp}')
      expect(el).toHaveValue('first question')
      // Once the caret moves into it, Up is the box's own until its first row.
      el.setSelectionRange(10, 10)
      expect(fireEvent.keyDown(el, { key: 'ArrowUp' })).toBe(true)
      expect(el).toHaveValue('first question')
    } finally {
      rows.mockRestore()
    }
  })

  it('leaves keys that compose text, and arrows with Shift, Ctrl, Alt or ⌘, to the box', async () => {
    backend()
    const el = await box()
    fireEvent.keyDown(el, { key: 'ArrowUp', isComposing: true })
    fireEvent.keyDown(el, { key: 'ArrowUp', keyCode: 229 })
    fireEvent.keyDown(el, { key: 'ArrowUp', shiftKey: true })
    fireEvent.keyDown(el, { key: 'ArrowUp', ctrlKey: true })
    fireEvent.keyDown(el, { key: 'ArrowUp', altKey: true })
    fireEvent.keyDown(el, { key: 'ArrowUp', metaKey: true })
    expect(el).toHaveValue('')
    fireEvent.keyDown(el, { key: 'ArrowUp' })
    expect(el).toHaveValue('second question')
  })

  it('lists each text once, newest first, without empty ones', () => {
    expect(recallList(['b', ' a ', ''], ['c', 'a', 'b ', '  '])).toEqual(['b', 'a', 'c'])
  })
})
