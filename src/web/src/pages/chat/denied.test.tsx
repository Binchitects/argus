import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { fakeApi, member, renderApp } from '@/test/utils'
import type { ChatConfig, Conversation } from './types'

const config: ChatConfig = {
  model: 'Main-Model',
  models: [{ name: 'Main-Model', context: 32768, maxOutput: 8192, vision: true, tools: true, thinking: true, loaded: true, prices: { input: null, cachedInput: null, output: null } }],
  presets: [],
  defaultThinking: null,
  codeIntel: false,
  tools: [],
  gitlabUrl: null,
  maxUploadBytes: 1024 * 1024,
  imageTypes: ['image/png'],
}

const conversation: Conversation = {
  id: 'c1', title: 'Sorting', thinking: null, tools: [], useCodeIntel: false, model: null, systemPrompt: null, temperature: null, topP: null, maxTokens: null,
  currentLeafId: 'a1', archivedAt: null, forkedFrom: null, createdAt: '', updatedAt: '',
  messages: [
    { id: 'q1', parentId: null, role: 'user', content: 'Which sort is stable?', attachments: [], createdAt: '' },
    { id: 'a1', parentId: 'q1', role: 'assistant', content: 'Merge sort.', attachments: [], createdAt: '', model: 'Main-Model' },
  ] as unknown as Conversation['messages'],
}

const chat = (denied: string[]) =>
  fakeApi(member, {
    'GET /api/chat/config': () => ({ json: { ...config, denied } }),
    'GET /api/chat/conversations': () => ({ json: [] }),
    'GET /api/chat/conversations/c1': () => ({ json: conversation }),
  })

describe('what a person may not use', () => {
  it('the chat offers it all to a person who holds every feature', async () => {
    chat([])
    renderApp('/chat/c1')
    expect(await screen.findByRole('button', { name: 'Attach' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Sketch' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /^Model: / })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'New room' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'New assistant' })).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Chat actions' }))
    for (const name of ['Share', 'Make it a room', 'Export']) expect(await screen.findByRole('menuitem', { name })).toBeInTheDocument()
  })

  it('the chat leaves out attachments, speech, the model, sharing, rooms and export for a person without them', async () => {
    chat(['feature.uploads', 'feature.speech', 'feature.model_choice', 'feature.share_links', 'feature.rooms.create', 'feature.export', 'feature.memory'])
    renderApp('/chat/c1')
    await screen.findByRole('textbox', { name: 'Message' })
    await waitFor(() => expect(screen.queryByRole('button', { name: 'Attach' })).not.toBeInTheDocument())
    for (const name of ['Talk', 'Sketch']) expect(screen.queryByRole('button', { name })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /^Model: / })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /^Memory/ })).not.toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Chat actions' }))
    expect(await screen.findByRole('menuitem', { name: 'Fork' })).toBeInTheDocument()
    for (const name of ['Share', 'Make it a room', 'Export']) expect(screen.queryByRole('menuitem', { name })).not.toBeInTheDocument()
  })

  it('no New room, New assistant or New skill for a person who may not make them', async () => {
    fakeApi({ ...member, denied: ['feature.rooms.create', 'feature.assistants.create', 'feature.skills'] }, {
      'GET /api/chat/config': () => ({ json: config }),
      'GET /api/chat/conversations': () => ({ json: [] }),
      'GET /api/chat/conversations/c1': () => ({ json: conversation }),
      'GET /api/assistants': () => ({ json: [] }),
      'GET /api/skills': () => ({ json: { mayShareCompany: false, mayShareGroups: true, mayTurnBuiltIn: false, groups: [], skills: [] } }),
    })
    const { router } = renderApp('/chat/c1')
    expect(await screen.findByRole('button', { name: 'New chat' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'New room' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'New assistant' })).not.toBeInTheDocument()
    await router.navigate('/assistants')
    expect(await screen.findByText('No assistants yet')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /New assistant/ })).not.toBeInTheDocument()
    await router.navigate('/skills')
    expect(await screen.findByText('No skills yet')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /New skill/ })).not.toBeInTheDocument()
  })
})
