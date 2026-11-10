import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { fakeApi, member, renderApp } from '@/test/utils'
import { blank } from '../chat/live'
import type { ChatConfig, Conversation } from '../chat/types'

const config: ChatConfig = {
  model: 'Main-Model',
  models: [{ name: 'Main-Model', context: 32768, maxOutput: 8192, vision: false, tools: true, thinking: true, loaded: true, prices: { input: 0.2, cachedInput: 0.02, output: 0.8 } }],
  presets: [],
  defaultThinking: null,
  codeIntel: false,
  tools: [],
  gitlabUrl: null,
  maxUploadBytes: 20 * 1024 * 1024,
  imageTypes: [],
}

const summary = (id: string, name: string, role: string, more: object = {}) => ({
  id, name, description: '', global: false, status: 'active', startsOn: null, dueOn: null, monthlyBudget: null, role, owner: 'Ann',
  mine: true, members: 2, chats: 0, goals: 0, goalsDone: 0, spentThisMonth: 0, updatedAt: '2026-11-01T10:00:00Z', ...more,
})

describe('a chat’s project', () => {
  it('is chosen from the chat’s menu among the projects the person may add to, and taken out again', async () => {
    let projectId: string | null = null
    const chat = (): Conversation => ({
      id: 'c1', title: 'Refunds', thinking: null, tools: [], useCodeIntel: false, model: null, systemPrompt: null, temperature: null, topP: null, maxTokens: null,
      currentLeafId: 'a1', archivedAt: null, forkedFrom: null, createdAt: '', updatedAt: '', projectId,
      messages: [{ ...blank('q1', 'user', null), status: 'complete', content: 'Hi' }, { ...blank('a1', 'assistant', 'q1'), status: 'complete', content: 'Hello' }],
    })
    const calls = fakeApi(member, {
      'GET /api/chat/config': () => ({ json: config }),
      'GET /api/chat/conversations': () => ({ json: [] }),
      'GET /api/assistants': () => ({ json: [] }),
      'GET /api/chat/conversations/c1': () => ({ json: chat() }),
      'GET /api/projects': () => ({
        json: [summary('p1', 'Payments', 'contributor'), summary('p2', 'Company wiki', 'contributor', { global: true }), summary('p3', 'Board', 'viewer'), summary('p4', 'Old', 'owner', { status: 'archived' })],
      }),
      'PUT /api/chat/conversations/c1/project': (body) => {
        projectId = (body as { projectId: string | null }).projectId
        return { json: {} }
      },
    })
    renderApp('/chat/c1')
    await userEvent.click(await screen.findByRole('button', { name: 'Chat actions' }))
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Project' }))
    // A viewer's project and an archived one take no chats.
    await screen.findByRole('menuitemcheckbox', { name: 'Payments' })
    expect(screen.getAllByRole('menuitemcheckbox').map((m) => m.textContent)).toEqual(['Payments', 'Company wiki'])
    await userEvent.click(screen.getByRole('menuitemcheckbox', { name: 'Payments' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'PUT')?.body).toEqual({ projectId: 'p1' }))

    await userEvent.click(await screen.findByRole('button', { name: 'Chat actions' }))
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Project: Payments' }))
    expect(await screen.findByRole('menuitemcheckbox', { name: 'Payments' })).toHaveAttribute('aria-checked', 'true')
    await userEvent.click(screen.getByRole('menuitem', { name: 'Take it out of Payments' }))
    await waitFor(() => expect(calls.filter((c) => c.method === 'PUT').at(-1)?.body).toEqual({ projectId: null }))
  })
})
