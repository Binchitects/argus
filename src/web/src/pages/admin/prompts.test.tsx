import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { visibleNavigation } from '@/app/nav'
import { admin, fakeApi, member, renderApp } from '@/test/utils'

const row = (n: number, source: 'chat' | 'api') => ({
  source, at: `2026-10-28T10:${String(59 - n).padStart(2, '0')}:00Z`,
  person: { id: 'p1', userName: 'ann', displayName: 'Ann' }, model: 'Qwen3-4B',
  question: `question ${n}`, answer: source === 'chat' ? `answer ${n}` : null,
  chat: source === 'chat' ? 'c1' : null, title: source === 'chat' ? 'Trip plans' : null, deleted: n === 1, tokens: source === 'api' ? 120 : null,
})

describe('prompt log', () => {
  it('lists questions and answers, searches by words, person and where, and pages to older ones', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/prompts': (_, __, url) => {
        if (url.searchParams.get('before')) return { json: { items: [row(9, 'api')], next: null } }
        return { json: { items: [row(0, 'chat'), row(1, 'chat'), row(2, 'api')], next: '2026-10-28T10:57:00Z' } }
      },
    })
    renderApp('/admin/prompts')
    const list = await screen.findByRole('list', { name: 'Prompt log' })
    const items = await within(list).findAllByRole('listitem')
    expect(items).toHaveLength(3)
    expect(within(items[0]).getByText('question 0')).toBeInTheDocument()
    expect(within(items[0]).getByText('answer 0')).toBeInTheDocument()
    expect(within(items[0]).getByText('in “Trip plans”')).toBeInTheDocument()
    expect(within(items[0]).getByRole('link', { name: 'Ann' })).toHaveAttribute('href', '/admin/people/p1')
    expect(within(items[1]).getByText('chat deleted')).toBeInTheDocument()
    expect(within(items[2]).getByText('API')).toBeInTheDocument()
    expect(within(items[2]).getByText('120 tokens')).toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Older' }))
    await waitFor(() => expect(within(list).getAllByRole('listitem')).toHaveLength(4))
    expect(calls.some((c) => c.path.includes('before=2026-10-28T10%3A57%3A00Z'))).toBe(true)

    await userEvent.type(screen.getByLabelText('Words'), 'budget')
    await userEvent.type(screen.getByLabelText('Person'), 'ann')
    await userEvent.click(screen.getByRole('button', { name: 'Search' }))
    await waitFor(() => expect(calls.some((c) => c.path === '/api/admin/prompts?source=all&q=budget&person=ann')).toBe(true))
    await userEvent.click(screen.getByRole('radio', { name: 'API' }))
    await waitFor(() => expect(calls.some((c) => c.path === '/api/admin/prompts?source=api&q=budget&person=ann')).toBe(true))
  })

  it('is in the menu only for those who may review prompts', () => {
    const titles = (me: Parameters<typeof visibleNavigation>[0]) => visibleNavigation(me).flatMap((s) => s.items.map((i) => i.title))
    expect(titles(admin)).toContain('Prompt log')
    expect(titles({ ...member, permissions: ['audit.read', 'usage.read'] })).not.toContain('Prompt log')
    expect(titles({ ...member, permissions: ['prompts.review'] })).toContain('Prompt log')
  })
})
