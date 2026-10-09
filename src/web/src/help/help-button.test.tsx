import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { admin, fakeApi, member, renderApp } from '@/test/utils'
import { besideQuery } from './help-button'

/** On a narrower screen (jsdom's, by default) the help opens over the page. */
async function openHelp() {
  await userEvent.click(await screen.findByRole('button', { name: 'Help for this page' }))
  return screen.findByRole('dialog')
}

/** A screen wide enough for the help to sit beside the page. */
function wideScreen() {
  vi.spyOn(window, 'matchMedia').mockImplementation(
    (query: string) => ({ matches: query === besideQuery, media: query, addEventListener() {}, removeEventListener() {} }) as unknown as MediaQueryList,
  )
}

describe('the help panel', () => {
  it('opens the page\'s help beside it: its parts, its tasks and the manual', async () => {
    fakeApi(admin)
    renderApp('/admin/people')
    const panel = await openHelp()
    expect(await within(panel).findByRole('heading', { name: 'People' })).toBeInTheDocument()
    expect(within(panel).getByText('Everyone who can sign in: their role, credit and sign-in security.')).toBeInTheDocument()
    expect(within(panel).getByText('Bulk actions')).toBeInTheDocument()
    expect(within(panel).getByRole('heading', { name: 'Add a person' })).toBeInTheDocument()
    expect(within(panel).getByRole('link', { name: 'Read more in the manual' })).toHaveAttribute('href', '/help/authentication#managing-people')
    expect(within(panel).getByRole('link', { name: 'Every page explained, in the manual' })).toHaveAttribute('href', '/help/pages#people')
  })

  it('opens over the page on a narrower screen, and Esc gives focus back to the ?', async () => {
    fakeApi(member)
    renderApp('/prompts')
    const panel = await openHelp()
    expect(await within(panel).findByRole('heading', { name: 'Prompts' })).toBeInTheDocument()
    await waitFor(() => expect(panel).toContainElement(document.activeElement as HTMLElement))
    await userEvent.keyboard('{Escape}')
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(screen.getByRole('button', { name: 'Help for this page' })).toHaveFocus()
  })

  it('sits beside the page on a wide screen: the page makes room, and it follows you while you work', async () => {
    wideScreen()
    fakeApi(member)
    renderApp('/prompts')
    const button = await screen.findByRole('button', { name: 'Help for this page' })
    await userEvent.click(button)
    const panel = await screen.findByRole('complementary', { name: 'Help' })
    expect(await within(panel).findByRole('heading', { name: 'Prompts' })).toBeInTheDocument()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(button).toHaveAttribute('aria-expanded', 'true')
    expect(panel).toHaveFocus()
    // A column of the page's row, beside the sidebar and the page: nothing of the page is under it.
    expect(panel.parentElement).toBe(screen.getByRole('complementary', { name: 'Sidebar' }).parentElement)
    expect(screen.getByRole('main')).not.toContainElement(panel)
    await userEvent.click(within(screen.getByRole('navigation', { name: 'Main' })).getByRole('link', { name: 'Assistants' }))
    expect(await within(panel).findByRole('heading', { name: 'Assistants' })).toBeInTheDocument()
    expect(screen.getByRole('complementary', { name: 'Help' })).toBe(panel)
    // Working in the page keeps focus there: the panel takes it only as it opens.
    expect(within(screen.getByRole('navigation', { name: 'Main' })).getByRole('link', { name: 'Assistants' })).toHaveFocus()
    // The ? closes it again.
    await userEvent.click(button)
    expect(screen.queryByRole('complementary', { name: 'Help' })).not.toBeInTheDocument()
    expect(button).toHaveAttribute('aria-expanded', 'false')
  })

  it('beside the page, an Esc in the page is the page\'s; Esc in the panel closes it', async () => {
    wideScreen()
    fakeApi(member)
    renderApp('/help')
    const button = await screen.findByRole('button', { name: 'Help for this page' })
    await userEvent.click(button)
    const panel = await screen.findByRole('complementary', { name: 'Help' })
    expect(await within(panel).findByRole('heading', { name: 'Manual' })).toBeInTheDocument()
    const search = screen.getByRole('searchbox', { name: 'Search the manual' })
    await userEvent.type(search, 'key')
    await userEvent.keyboard('{Escape}')
    expect(screen.getByRole('complementary', { name: 'Help' })).toBe(panel)
    await userEvent.click(within(panel).getByRole('heading', { name: 'Manual' }))
    await userEvent.keyboard('{Escape}')
    expect(screen.queryByRole('complementary', { name: 'Help' })).not.toBeInTheDocument()
    // Focus was in the panel: it goes back to the ?.
    expect(button).toHaveFocus()
  })

  it('names the group of Settings the address points at', async () => {
    fakeApi(admin)
    renderApp('/admin/settings#company-directory-ldap')
    const panel = await openHelp()
    expect(await within(panel).findByRole('heading', { name: 'This group: Company directory (LDAP)' })).toBeInTheDocument()
    const groups = within(panel).getByRole('region', { name: 'Its groups' })
    expect(within(groups).getByText('Safeguards')).toBeInTheDocument()
    expect(groups.querySelector('[aria-current="true"]')).toHaveTextContent('Company directory (LDAP)')
  })

  it('gives a member the help around every page on an admin\'s page', async () => {
    fakeApi(member)
    renderApp('/admin/models')
    const panel = await openHelp()
    expect(await within(panel).findByRole('heading', { name: 'Around every page' })).toBeInTheDocument()
    expect(within(panel).queryByText('Keep loaded')).not.toBeInTheDocument()
  })

  it('opens the manual at its section, and gets out of the way', async () => {
    fakeApi(member)
    const { router } = renderApp('/prompts')
    const panel = await openHelp()
    await userEvent.click(await within(panel).findByRole('link', { name: 'Read more in the manual' }))
    await waitFor(() => expect(router.state.location.pathname).toBe('/help/chat'))
    expect(router.state.location.hash).toBe('#prompts-and-slash-commands')
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(await screen.findByRole('heading', { name: 'Prompts and slash commands' })).toHaveAttribute('id', 'prompts-and-slash-commands')
  })

  it('helps on the sign-in page too, with whom to ask instead of the manual', async () => {
    fakeApi(null, { 'GET /api/info': () => ({ json: { name: 'Argus Arena', version: '1.2.3', supportContact: 'help@example.test' } }) })
    renderApp('/login')
    const panel = await openHelp()
    expect(await within(panel).findByRole('heading', { name: 'Signing in' })).toBeInTheDocument()
    expect(within(panel).queryByRole('link', { name: 'Read more in the manual' })).not.toBeInTheDocument()
    expect(within(panel).getByRole('link', { name: 'Ask help@example.test' })).toHaveAttribute('href', 'mailto:help@example.test')
  })

  it('has the manual at the foot of the sidebar and in the account menu', async () => {
    fakeApi(member)
    const { router } = renderApp('/')
    const sidebar = await screen.findByRole('complementary', { name: 'Sidebar' })
    expect(within(sidebar).getByRole('link', { name: 'Manual' })).toHaveAttribute('href', '/help')
    await userEvent.click(screen.getByRole('button', { name: /Account menu/ }))
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Manual' }))
    await waitFor(() => expect(router.state.location.pathname).toBe('/help'))
  })
})
