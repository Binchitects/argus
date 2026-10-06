import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { canRead, manualDocs, parseDoc } from '@/help/manual'
import { pagesMarkdown } from '@/help/pages-doc'
import { admin, fakeApi, member, renderApp } from '@/test/utils'

const contents = () => screen.findByRole('navigation', { name: 'Contents of the manual' })

describe('the manual', () => {
  it('lists the pages a member may read, and an admin\'s too for admins', async () => {
    fakeApi(member)
    const view = renderApp('/help')
    let nav = await contents()
    expect(within(nav).getByRole('link', { name: 'Chat' })).toHaveAttribute('href', '/help/chat')
    expect(within(nav).getByRole('link', { name: 'Every page, explained' })).toBeInTheDocument()
    expect(within(nav).queryByRole('link', { name: 'Admin, usage and cost' })).not.toBeInTheDocument()
    view.unmount()

    fakeApi(admin)
    renderApp('/help')
    nav = await contents()
    expect(within(nav).getByRole('link', { name: 'Admin, usage and cost' })).toHaveAttribute('href', '/help/admin')
  })

  it('searches every page and opens a result at its heading', async () => {
    fakeApi(admin)
    const { router } = renderApp('/help')
    await userEvent.type(await screen.findByRole('searchbox', { name: 'Search the manual' }), 'legal hold')
    const results = await screen.findByRole('region', { name: 'Search results' })
    const first = within(results).getAllByRole('link')[0]
    expect(first).toHaveTextContent('Retention, legal hold and exports')
    expect(within(first).getAllByText(/legal|hold/i, { selector: 'mark' }).length).toBeGreaterThan(0)
    await userEvent.click(first)
    await waitFor(() => expect(router.state.location.pathname).toBe('/help/admin'))
    expect(router.state.location.hash).toBe('#retention-legal-hold-and-exports')
    expect(await screen.findByRole('heading', { name: 'Retention, legal hold and exports' })).toHaveAttribute('id', 'retention-legal-hold-and-exports')
  })

  it('says when nothing matches', async () => {
    fakeApi(member)
    renderApp('/help?q=zzqqxx')
    expect(await screen.findByText('Nothing in the manual matches')).toBeInTheDocument()
  })

  it('keeps links between pages in the app', async () => {
    fakeApi(admin)
    const { router } = renderApp('/help/chat')
    const article = await screen.findByRole('article', { name: 'Chat' })
    const link = (await within(article).findAllByRole('link', { name: 'knowledge.md' }))[0]
    expect(link).toHaveAttribute('href', '/help/knowledge')
    await userEvent.click(link)
    await waitFor(() => expect(router.state.location.pathname).toBe('/help/knowledge'))
    expect(await screen.findByRole('heading', { name: 'Company knowledge', level: 1 })).toBeInTheDocument()
  })

  it('shows a member a link to an admins\' page as its words', async () => {
    fakeApi(member)
    renderApp('/help/chat')
    const article = await screen.findByRole('article', { name: 'Chat' })
    await within(article).findByRole('heading', { name: 'What it does' })
    expect(within(article).queryByRole('link', { name: 'knowledge.md' })).not.toBeInTheDocument()
    expect(within(article).getAllByTitle('A page for admins')[0]).toHaveTextContent('knowledge.md')
  })

  it('shows a file of the repository it does not hold as its name', async () => {
    fakeApi(admin)
    renderApp('/help/settings')
    const article = await screen.findByRole('article', { name: 'Settings' })
    const licence = await within(article).findByTitle('In the source, not in the manual: LICENSING.md')
    expect(licence).toHaveTextContent('LICENSING.md')
    expect(within(article).queryByRole('link', { name: 'LICENSING.md' })).not.toBeInTheDocument()
  })

  it('has every page\'s help as a page of its own', async () => {
    fakeApi(member)
    renderApp('/help/pages#chat')
    const article = await screen.findByRole('article', { name: 'Every page, explained' })
    expect(await within(article).findByRole('heading', { name: 'Chat', level: 3 })).toHaveAttribute('id', 'chat')
    expect(within(article).queryByRole('heading', { name: 'People', level: 3 })).not.toBeInTheDocument()
  })

  it('opens a page by its file\'s name too', async () => {
    fakeApi(admin)
    renderApp('/help/argus/README.md')
    expect(await screen.findByRole('article', { name: 'Argus in the platform' })).toBeInTheDocument()
  })

  it('says when a page is not in the manual', async () => {
    fakeApi(member)
    renderApp('/help/plan')
    expect(await screen.findByText('This page is not in the manual')).toBeInTheDocument()
  })

  it('gives every heading of every page the anchor its contents and search link to', { timeout: 120_000 }, async () => {
    const pages = [...manualDocs.filter((d) => canRead(d, true)).map((d) => ({ id: d.id, title: d.title, text: d.text })), { id: 'pages', title: 'Every page, explained', text: pagesMarkdown(true) }]
    for (const page of pages) {
      fakeApi(admin)
      const view = renderApp(`/help/${page.id}`)
      const article = await screen.findByRole('article', { name: page.title })
      const expected = parseDoc(page.text).headings.map((h) => h.id)
      await waitFor(() => expect(article.querySelectorAll('h1, h2, h3, h4, h5, h6')).toHaveLength(expected.length))
      expect([...article.querySelectorAll('h1, h2, h3, h4, h5, h6')].map((h) => h.id), page.id).toEqual(expected)
      view.unmount()
    }
  })
})
