import { cleanup, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import type { Me } from '@/lib/api'
import { admin, fakeApi, member, renderApp } from '@/test/utils'
import type { ChatConfig } from './types'

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

function backend(me: Me) {
  fakeApi(me, {
    'GET /api/chat/config': () => ({ json: config }),
    'GET /api/chat/conversations': (_b, _i, url) => {
      const q = url.searchParams.get('q')?.toLowerCase()
      return { json: chats.filter((c) => !q || c.title.toLowerCase().includes(q)) }
    },
    'GET /api/assistants': () => ({ json: [] }),
  })
}

async function openList(me: Me = member) {
  backend(me)
  renderApp('/chat')
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
    expect(within(list).getByRole('button', { name: 'Assistants' })).toHaveAttribute('aria-expanded', 'false')
    expect(within(list).getByRole('button', { name: 'Today' })).toHaveAttribute('aria-expanded', 'true')
    cleanup()

    list = await openList(admin)
    expect(within(list).getByRole('button', { name: 'Previous 7 days' })).toHaveAttribute('aria-expanded', 'true')
    expect(within(list).getByRole('link', { name: 'Old notes' })).toBeInTheDocument()
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

  it('the whole list folds to a rail and back, the focus going along, and stays folded', async () => {
    let list = await openList()
    const collapse = within(list).getByRole('button', { name: 'Collapse chat list' })
    expect(collapse).toHaveAttribute('aria-expanded', 'true')
    collapse.focus()
    await userEvent.keyboard('{Enter}')

    const expand = await within(list).findByRole('button', { name: 'Expand chat list' })
    expect(expand).toHaveAttribute('aria-expanded', 'false')
    await waitFor(() => expect(expand).toHaveFocus())
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
    await waitFor(() => expect(within(list).getByRole('button', { name: 'Collapse chat list' })).toHaveFocus())
    expect(await within(list).findByRole('link', { name: 'Plan the launch' })).toBeInTheDocument()
    expect(list.closest('.grid')?.className).toContain('lg:grid-cols-[16rem_minmax(0,1fr)]')
  })
})
