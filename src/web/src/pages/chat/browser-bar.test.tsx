import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { fakeApi, member, renderApp } from '@/test/utils'
import type { ChatConfig, Conversation } from './types'

const config: ChatConfig = {
  model: 'Main-Model',
  models: [{ name: 'Main-Model', context: 32768, maxOutput: 8192, vision: true, tools: true, thinking: false, loaded: true, prices: { input: 0.2, cachedInput: 0.02, output: 0.8 } }],
  presets: [{ level: 'off', label: 'No thinking' }],
  defaultThinking: 'off',
  argus: false,
  tools: [{ id: 'browser', title: 'Browser', description: 'Uses a real browser.', icon: 'globe', onByDefault: false, askFirst: false }],
  gitlabUrl: null,
  maxUploadBytes: 20 * 1024 * 1024,
  imageTypes: ['image/png'],
}

const conversation: Conversation = {
  id: 'c1', title: 'Shopping', thinking: null, tools: ['browser'], useArgus: false, model: null, systemPrompt: null, temperature: null, topP: null, maxTokens: null,
  currentLeafId: null, archivedAt: null, forkedFrom: null, createdAt: '', updatedAt: '', messages: [],
}

afterEach(() => vi.restoreAllMocks())

describe('the browser agent', () => {
  it('shows the chat’s browser live, and the person takes it over, clicks, types and hands it back', async () => {
    let takenOver = false
    let open = true
    const calls = fakeApi(member, {
      'GET /api/chat/config': () => ({ json: config }),
      'GET /api/chat/conversations': () => ({ json: [conversation] }),
      'GET /api/chat/conversations/c1': () => ({ json: conversation }),
      'GET /api/chat/browser/c1': () => ({ json: open ? { open: true, url: 'https://shop.test/sign-in', title: 'Sign in', takenOver, width: 1280, height: 800 } : { open: false } }),
      'POST /api/chat/browser/c1/take': () => {
        takenOver = true
        return { status: 204 }
      },
      'POST /api/chat/browser/c1/give': () => {
        takenOver = false
        return { status: 204 }
      },
      'POST /api/chat/browser/c1/input': () => ({ status: 204 }),
      'DELETE /api/chat/browser/c1': () => {
        open = false
        return { status: 204 }
      },
    })
    renderApp('/chat/c1')
    const bar = await screen.findByRole('region', { name: "The chat's browser" })
    expect(bar).toHaveTextContent('Sign in')
    expect(bar).toHaveTextContent('https://shop.test/sign-in')

    await userEvent.click(within(bar).getByRole('button', { name: /Take over/ }))
    const live = await screen.findByRole('dialog')
    const page = await within(live).findByRole('button', { name: 'Click on the page where you point' })
    vi.spyOn(page, 'getBoundingClientRect').mockReturnValue({ left: 0, top: 0, width: 800, height: 500, right: 800, bottom: 500, x: 0, y: 0, toJSON: () => ({}) })
    fireEvent.click(page, { clientX: 400, clientY: 125 })
    await waitFor(() => expect(calls.find((c) => c.path === '/api/chat/browser/c1/input')?.body).toEqual({ kind: 'click', x: 0.5, y: 0.25 }))
    await userEvent.type(within(live).getByLabelText('Type into the page'), 'my secret')
    await userEvent.click(within(live).getByRole('button', { name: 'Type' }))
    await waitFor(() => expect(calls.some((c) => c.path === '/api/chat/browser/c1/input' && (c.body as { kind: string }).kind === 'type')).toBe(true))
    expect(calls.filter((c) => c.path === '/api/chat/browser/c1/input').map((c) => c.body)).toContainEqual({ kind: 'type', text: 'my secret' })
    await userEvent.click(within(live).getByRole('button', { name: /Hand it back/ }))
    await waitFor(() => expect(calls.some((c) => c.path === '/api/chat/browser/c1/give')).toBe(true))
    await userEvent.keyboard('{Escape}')

    await userEvent.click(within(await screen.findByRole('region', { name: "The chat's browser" })).getByRole('button', { name: 'Close the browser' }))
    await waitFor(() => expect(screen.queryByRole('region', { name: "The chat's browser" })).toBeNull())
  })
})
