import { cleanup, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import type { Me } from '@/lib/api'
import { admin, fakeApi, member, renderApp } from '@/test/utils'
import type { AssistantSummary, ChatConfig, Conversation } from './types'

const config: ChatConfig = {
  model: 'Main-Model',
  models: [{ name: 'Main-Model', context: 32768, maxOutput: 8192, vision: false, tools: true, thinking: true, loaded: true, prices: { input: 0.2, cachedInput: 0.02, output: 0.8 } }],
  presets: [],
  defaultThinking: null,
  argus: false,
  tools: [],
  gitlabUrl: null,
  maxUploadBytes: 1024,
  imageTypes: [],
}

const daysAgo = (n: number) => new Date(Date.now() - n * 86_400_000).toISOString()
const chats = [
  { id: 'c1', title: 'Plan the launch', updatedAt: daysAgo(0) },
  { id: 'c2', title: 'Fix the build', updatedAt: daysAgo(0), answering: true },
  { id: 'c3', title: 'Old notes', updatedAt: daysAgo(3) },
]

const assistant = (id: string, name: string): AssistantSummary => ({
  id, name, description: null, icon: 'bot', color: 'blue', reach: 'Private', owner: null, mine: true, canEdit: true, chats: 1, people: 1, myChats: 1, files: 0, updatedAt: daysAgo(1),
})
const planChat: Conversation = {
  ...chats[0], answering: false, thinking: null, tools: [], useArgus: false, model: null, systemPrompt: null, temperature: null, topP: null, maxTokens: null,
  currentLeafId: null, archivedAt: null, forkedFrom: null, createdAt: daysAgo(0), messages: [],
}

function backend(me: Me, assistants: AssistantSummary[] = []) {
  fakeApi(me, {
    'GET /api/chat/config': () => ({ json: config }),
    'GET /api/chat/conversations': (_b, _i, url) => {
      const q = url.searchParams.get('q')?.toLowerCase()
      return { json: chats.filter((c) => !q || c.title.toLowerCase().includes(q)) }
    },
    'GET /api/chat/conversations/c1': () => ({ json: planChat }),
    'GET /api/assistants': () => ({ json: assistants }),
  })
}

async function openList(me: Me = member, path = '/chat') {
  backend(me)
  renderApp(path)
  const list = await screen.findByRole('navigation', { name: 'Chats' })
  await within(list).findByRole('link', { name: 'Plan the launch' })
  return list
}

describe('chat list', () => {
  it('folds a group under its heading from the keyboard, counting what it hides', async () => {
    const list = await openList()
    const today = within(list).getByRole('button', { name: 'Today' })
    expect(today).toHaveAttribute('aria-expanded', 'true')
    expect(document.getElementById(today.getAttribute('aria-controls')!)).toContainElement(within(list).getByRole('link', { name: 'Plan the launch' }))

    today.focus()
    await userEvent.keyboard('{Enter}')
    expect(today).toHaveAttribute('aria-expanded', 'false')
    expect(today).toHaveAccessibleName('Today, 2 chats, 1 answering')
    expect(within(list).queryByRole('link', { name: 'Plan the launch' })).toBeNull()
    // Other groups are left as they were.
    expect(within(list).getByRole('link', { name: 'Old notes' })).toBeInTheDocument()

    await userEvent.keyboard(' ')
    expect(today).toHaveAttribute('aria-expanded', 'true')
    expect(within(list).getByRole('link', { name: 'Plan the launch' })).toBeInTheDocument()
  })

  it('remembers folded groups for the person on this browser, not for someone else', async () => {
    let list = await openList()
    await userEvent.click(within(list).getByRole('button', { name: 'Previous 7 days' }))
    await userEvent.click(within(list).getByRole('button', { name: 'Assistants' }))
    expect(within(list).queryByRole('link', { name: 'Old notes' })).toBeNull()
    cleanup()

    list = await openList()
    expect(within(list).getByRole('button', { name: /^Previous 7 days/ })).toHaveAttribute('aria-expanded', 'false')
    expect(within(list).getByRole('button', { name: 'Assistants, 0 assistants' })).toHaveAttribute('aria-expanded', 'false')
    expect(within(list).getByRole('button', { name: 'Today' })).toHaveAttribute('aria-expanded', 'true')
    cleanup()

    list = await openList(admin)
    expect(within(list).getByRole('button', { name: 'Previous 7 days' })).toHaveAttribute('aria-expanded', 'true')
    expect(within(list).getByRole('link', { name: 'Old notes' })).toBeInTheDocument()
  })

  it('a folded group keeps the open chat in sight, and the folded assistants count theirs', async () => {
    const list = await openList(member, '/chat/c1')
    await userEvent.click(within(list).getByRole('button', { name: 'Today' }))
    const today = within(list).getByRole('button', { name: /^Today/ })
    expect(today).toHaveAccessibleName('Today, 2 chats, 1 answering')
    // The open chat stays under its heading; the rest fold away.
    const shown = within(list).getByRole('link', { name: 'Plan the launch' })
    expect(shown).toHaveAttribute('aria-current', 'page')
    expect(document.getElementById(today.getAttribute('aria-controls')!)).toContainElement(shown)
    expect(within(list).queryByRole('link', { name: /Fix the build/ })).toBeNull()
    cleanup()

    backend(member, [assistant('a1', 'Reviewer'), assistant('a2', 'Writer')])
    renderApp('/chat/assistants/a2')
    const again = await screen.findByRole('navigation', { name: 'Chats' })
    await userEvent.click(await within(again).findByRole('button', { name: 'Assistants' }))
    expect(within(again).getByRole('button', { name: /^Assistants/ })).toHaveAccessibleName('Assistants, 2 assistants')
    expect(within(again).getByRole('link', { name: /Writer/ })).toBeInTheDocument()
    expect(within(again).queryByRole('link', { name: /Reviewer/ })).toBeNull()
  })

  it('a search shows its matches in folded groups too', async () => {
    const list = await openList()
    await userEvent.click(within(list).getByRole('button', { name: 'Previous 7 days' }))
    await userEvent.type(within(list).getByRole('searchbox', { name: 'Search chats' }), 'old')
    expect(await within(list).findByRole('link', { name: 'Old notes' })).toBeInTheDocument()
    expect(within(list).queryByRole('button', { name: /Previous 7 days/ })).toBeNull()
    await userEvent.clear(within(list).getByRole('searchbox', { name: 'Search chats' }))
    expect(await within(list).findByRole('button', { name: /^Previous 7 days/ })).toHaveAttribute('aria-expanded', 'false')
  })

  it('the whole list folds to a rail and back, the focus staying on its button, and stays folded', async () => {
    let list = await openList()
    const collapse = within(list).getByRole('button', { name: 'Collapse chat list' })
    expect(collapse).toHaveAttribute('aria-expanded', 'true')
    collapse.focus()
    await userEvent.keyboard('{Enter}')

    // One button in one place does both: the focus never has to move.
    const expand = await within(list).findByRole('button', { name: 'Expand chat list' })
    expect(expand).toBe(collapse)
    expect(expand).toHaveAttribute('aria-expanded', 'false')
    expect(expand).toHaveFocus()
    expect(within(list).queryByRole('searchbox', { name: 'Search chats' })).toBeNull()
    expect(within(list).queryByRole('link', { name: 'Plan the launch' })).toBeNull()
    // A new chat and the advanced search stay a click away.
    expect(within(list).getByRole('button', { name: 'New chat' })).toBeInTheDocument()
    expect(within(list).getByRole('button', { name: 'Advanced search' })).toBeInTheDocument()
    expect(list.closest('.grid')?.className).toContain('lg:grid-cols-[3rem_minmax(0,1fr)]')
    cleanup()

    backend(member)
    renderApp('/chat')
    list = await screen.findByRole('navigation', { name: 'Chats' })
    const again = await within(list).findByRole('button', { name: 'Expand chat list' })
    again.focus()
    await userEvent.keyboard(' ')
    expect(again).toHaveFocus()
    expect(again).toHaveAccessibleName('Collapse chat list')
    expect(await within(list).findByRole('link', { name: 'Plan the launch' })).toBeInTheDocument()
    expect(list.closest('.grid')?.className).toContain('lg:grid-cols-[16rem_minmax(0,1fr)]')
  })

  it('folding the list with the mouse leaves no hint open over the chat', async () => {
    const list = await openList()
    await userEvent.click(within(list).getByRole('button', { name: 'Collapse chat list' }))
    const expand = within(list).getByRole('button', { name: 'Expand chat list' })
    // Past the hint's delay, and after the pointer has gone: no hint is left open.
    await new Promise((r) => setTimeout(r, 400))
    expect(screen.queryByRole('tooltip')).toBeNull()
    await userEvent.unhover(expand)
    await userEvent.hover(screen.getByRole('main'))
    await new Promise((r) => setTimeout(r, 400))
    expect(screen.queryByRole('tooltip')).toBeNull()

    await userEvent.click(expand)
    expect(within(list).getByRole('button', { name: 'Collapse chat list' })).toBeInTheDocument()
    await new Promise((r) => setTimeout(r, 400))
    expect(screen.queryByRole('tooltip')).toBeNull()
  })
})
