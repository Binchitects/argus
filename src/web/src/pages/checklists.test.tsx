import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { fakeApi, member, renderApp } from '@/test/utils'
import { blank } from './chat/live'
import type { ChatConfig, Conversation } from './chat/types'

const list = { id: 'k1', name: 'Done means done', description: 'Before a change is done', items: ['The tests pass', 'The docs say what changed'], createdAt: '', updatedAt: '' }

const config: ChatConfig = {
  model: 'Main-Model',
  models: [{ name: 'Main-Model', context: 32768, maxOutput: 8192, vision: false, tools: true, thinking: false, loaded: true, prices: { input: 0.2, cachedInput: 0.02, output: 0.8 } }],
  presets: [{ level: 'off', label: 'No thinking' }],
  defaultThinking: 'off',
  argus: false,
  tools: [],
  gitlabUrl: null,
  maxUploadBytes: 20 * 1024 * 1024,
  imageTypes: ['image/png'],
}

const conversation: Conversation = {
  id: 'c1', title: 'Fix it', thinking: null, tools: [], useArgus: false, model: null, systemPrompt: null, temperature: null, topP: null, maxTokens: null,
  currentLeafId: 'a1', archivedAt: null, forkedFrom: null, createdAt: '', updatedAt: '',
  messages: [blank('q1', 'user', null, 'Fix the parser'), { ...blank('a1', 'assistant', 'q1', 'Done.'), model: 'Main-Model' }],
}

const run = (done: boolean[]) => ({
  id: 'r1', checklistId: 'k1', name: 'Done means done', done: done.filter(Boolean).length, total: 2,
  items: list.items.map((text, n) => ({ number: n + 1, text, done: done[n], note: done[n] ? (n === 0 ? '42 of 42 pass' : null) : null, by: done[n] ? (n === 0 ? 'model' : 'person') : null, doneAt: null })),
})

describe('checklists', () => {
  it('one is written with its items one a line, listed, and deleted after asking', async () => {
    let lists: (typeof list)[] = []
    const calls = fakeApi(member, {
      'GET /api/checklists': () => ({ json: lists }),
      'POST /api/checklists': (body) => {
        lists = [{ ...list, ...(body as object) }]
        return { status: 201, json: lists[0] }
      },
      'DELETE /api/checklists/k1': () => {
        lists = []
        return { status: 204 }
      },
    })
    renderApp('/checklists')
    await userEvent.click(await screen.findByRole('button', { name: 'New checklist' }))
    const dialog = await screen.findByRole('dialog', { name: 'New checklist' })
    await userEvent.type(within(dialog).getByLabelText('Name'), 'Done means done')
    await userEvent.type(within(dialog).getByLabelText('Items'), 'The tests pass{Enter}{Enter}  The docs say what changed  ')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'POST')?.body).toEqual({ name: 'Done means done', description: '', items: ['The tests pass', 'The docs say what changed'] }))
    expect(await screen.findByText('The docs say what changed')).toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Delete Done means done' }))
    await userEvent.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Delete' }))
    await waitFor(() => expect(calls.some((c) => c.method === 'DELETE')).toBe(true))
  })

  it('turned on in a chat, its items show who ticked them and how, and the person ticks too', async () => {
    let on: ReturnType<typeof run> | null = null
    const calls = fakeApi(member, {
      'GET /api/chat/config': () => ({ json: config }),
      'GET /api/chat/conversations': () => ({ json: [conversation] }),
      'GET /api/chat/conversations/c1': () => ({ json: conversation }),
      'GET /api/checklists': () => ({ json: [list] }),
      'GET /api/chat/conversations/c1/checklist': () => (on ? { json: on } : { status: 204 }),
      'PUT /api/chat/conversations/c1/checklist': () => {
        on = run([true, false])
        return { json: on }
      },
      'POST /api/chat/conversations/c1/checklist/tick': () => {
        on = run([true, true])
        return { json: on }
      },
    })
    renderApp('/chat/c1')
    await userEvent.click(await screen.findByRole('button', { name: 'Checklist' }))
    await userEvent.click(within(await screen.findByRole('list', { name: 'Your checklists' })).getByRole('button', { name: /Done means done/ }))
    await waitFor(() => expect(calls.find((c) => c.method === 'PUT')?.body).toEqual({ checklistId: 'k1' }))
    const items = await screen.findByRole('list', { name: 'Checklist items' })
    expect(within(items).getByText('The model: 42 of 42 pass')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Checklist Done means done (1 of 2)' })).toBeInTheDocument()

    await userEvent.click(within(items).getByRole('checkbox', { name: 'The docs say what changed' }))
    await waitFor(() => expect(calls.find((c) => c.path.endsWith('/checklist/tick'))?.body).toEqual({ item: 2, done: true }))
    expect(await screen.findByRole('button', { name: 'Checklist Done means done (2 of 2)' })).toBeInTheDocument()
  })
})
