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
  state: 'done', error: null, durationMs: 1500,
})

describe('prompt log', () => {
  it("shows a question's pictures and the files its answer made, each read from the log's own address", async () => {
    const file = (id: string, name: string, kind: string, contentType: string) => ({ id, name, kind, contentType, size: 2048, url: `/api/admin/prompts/files/chat/${id}` })
    fakeApi(admin, {
      'GET /api/admin/prompts': () => ({
        json: {
          items: [{ ...row(0, 'chat'), files: [file('f1', 'diagram.png', 'image', 'image/png')], made: [file('f2', 'report.csv', 'text', 'text/csv'), file('f3', 'chart.png', 'image', 'image/png')] }],
          next: null,
        },
      }),
    })
    renderApp('/admin/prompts')
    const asked = await screen.findByRole('region', { name: 'With the question' })
    expect(within(asked).getByRole('img', { name: 'diagram.png' })).toHaveAttribute('src', '/api/admin/prompts/files/chat/f1')
    const made = screen.getByRole('region', { name: 'Made by the answer' })
    expect(within(made).getByRole('img', { name: 'chart.png' })).toHaveAttribute('src', '/api/admin/prompts/files/chat/f3')
    expect(within(made).getByRole('link', { name: /report\.csv/ })).toHaveAttribute('href', '/api/admin/prompts/files/chat/f2')
  })

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
    expect(within(items[2]).getByText(/120 tokens/)).toBeInTheDocument()

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

  it('shows each state with why it failed, an answer being written, and the whole of a long answer on Show all', async () => {
    const long = Array.from({ length: 30 }, (_, k) => `line ${k}`).join('\n')
    const calls = fakeApi(admin, {
      'GET /api/admin/prompts': () => ({
        json: {
          items: [
            { ...row(0, 'api'), state: 'running', answer: null, durationMs: null },
            { ...row(1, 'api'), state: 'failed', error: 'Budget has been exceeded!' },
            { ...row(2, 'api'), state: 'refused', error: 'The line is full: try again in a moment.' },
            { ...row(3, 'api'), state: 'stopped', answer: 'Once upon', error: 'The caller went away before the answer was whole.' },
            { ...row(4, 'chat'), answer: long },
          ],
          next: null,
        },
      }),
    })
    renderApp('/admin/prompts')
    const items = await within(await screen.findByRole('list', { name: 'Prompt log' })).findAllByRole('listitem')
    expect(within(items[0]).getByText('Running')).toBeInTheDocument()
    expect(within(items[0]).getByText('Being answered…')).toBeInTheDocument()
    expect(within(items[1]).getByText('Failed')).toBeInTheDocument()
    expect(within(items[1]).getByRole('note')).toHaveTextContent('Budget has been exceeded!')
    expect(within(items[2]).getByText('Refused')).toBeInTheDocument()
    expect(within(items[3]).getByRole('region', { name: 'Answer, as far as it went' })).toHaveTextContent('Once upon')
    expect(within(items[4]).getByText('took 1.50 s', { exact: false })).toBeInTheDocument()

    const answer = within(items[4]).getByRole('region', { name: 'Answer' })
    const more = within(answer).getByRole('button', { name: /Show all/ })
    expect(more).toHaveAttribute('aria-expanded', 'false')
    await userEvent.click(more)
    expect(within(answer).getByRole('button', { name: 'Show less' })).toHaveAttribute('aria-expanded', 'true')

    await userEvent.click(screen.getByRole('combobox', { name: 'State' }))
    await userEvent.click(await screen.findByRole('option', { name: 'Failed' }))
    await waitFor(() => expect(calls.some((c) => c.path === '/api/admin/prompts?source=all&state=failed')).toBe(true))
  })

  it('is in the menu only for those who may review prompts', () => {
    const titles = (me: Parameters<typeof visibleNavigation>[0]) => visibleNavigation(me).flatMap((s) => s.items.map((i) => i.title))
    expect(titles(admin)).toContain('Prompt log')
    expect(titles({ ...member, permissions: ['audit.read', 'usage.read'] })).not.toContain('Prompt log')
    expect(titles({ ...member, permissions: ['prompts.review'] })).toContain('Prompt log')
  })

  it('tags a question, summarizes it, searches by tag and reports by model, person and tag', async () => {
    let tags: string[] = []
    let brief: string | null = null
    const calls = fakeApi(admin, {
      'GET /api/tags': () => ({ json: { known: ['review', 'security'], used: [{ tag: 'later', count: 2 }] } }),
      'GET /api/admin/prompts': () => ({ json: { items: [{ ...row(0, 'chat'), id: 'm1', tags, brief }], next: null } }),
      'PUT /api/admin/prompts/chat/m1/tags': (body: { tags: string[] }) => {
        tags = body.tags
        return { json: { tags } }
      },
      'POST /api/admin/prompts/chat/m1/summarize': () => {
        brief = 'Asks about the trip.'
        tags = [...tags, 'security']
        return { json: { brief, tags } }
      },
      'GET /api/admin/prompts/report': () => ({
        json: {
          api: 4, apiFailed: 1, apiRefused: 0, chat: 3, chatFailed: 0, apiP95Ms: 2500, chatP95Ms: 4000,
          apiModels: [{ name: 'Qwen3-4B', count: 4, failed: 1, stopped: 0, refused: 0, averageMs: 1000, medianMs: 900, p95Ms: 2500, tokens: 480, api: 0, chat: 0 }],
          chatModels: [], people: [{ name: 'Ann', count: 7, failed: 0, stopped: 0, refused: 0, averageMs: null, medianMs: null, p95Ms: null, tokens: 0, api: 4, chat: 3 }],
          tags: [{ name: 'security', count: 2, failed: 0, stopped: 0, refused: 0, averageMs: null, medianMs: null, p95Ms: null, tokens: 0, api: 0, chat: 2 }], days: [],
        },
      }),
    })
    renderApp('/admin/prompts')
    await userEvent.click(await screen.findByRole('button', { name: 'Tags and summary' }))
    const dialog = await screen.findByRole('dialog', { name: /Tags and summary/ })
    await userEvent.click(within(dialog).getByRole('button', { name: '+ review' }))
    await userEvent.type(within(dialog).getByRole('textbox', { name: 'Tags' }), 'For Later{Enter}')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save tags' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'PUT')?.body).toEqual({ tags: ['review', 'for-later'] }))
    expect(await screen.findByText('#for-later')).toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Tags and summary' }))
    const again = await screen.findByRole('dialog', { name: /Tags and summary/ })
    await userEvent.click(within(again).getByRole('button', { name: /Summarize and tag/ }))
    expect(await within(again).findByLabelText('Summary')).toHaveTextContent('Asks about the trip.')
    expect(within(again).getByText('#security')).toBeInTheDocument()
    await userEvent.keyboard('{Escape}')

    await userEvent.click(screen.getByRole('combobox', { name: 'Tag' }))
    await userEvent.click(await screen.findByRole('option', { name: '#later' }))
    await waitFor(() => expect(calls.some((c) => c.path.startsWith('/api/admin/prompts?') && c.path.includes('tag=later'))).toBe(true))

    await userEvent.click(screen.getByRole('tab', { name: 'Report' }))
    const byModel = await screen.findByRole('region', { name: 'API requests by model' })
    expect(within(byModel).getByRole('row', { name: /Qwen3-4B/ })).toHaveTextContent('25.0%')
    expect(within(screen.getByRole('region', { name: 'By tag' })).getByRole('row', { name: /security/ })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: /CSV/ })).toHaveAttribute('href', expect.stringContaining('format=csv'))
  })
})
