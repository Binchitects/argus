import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { fakeApi, member, renderApp } from '@/test/utils'

describe('account', () => {
  it('someone who signs in with the company account changes their password and two-factor sign-in there', async () => {
    fakeApi({ ...member, source: 'oidc' })
    renderApp('/account')
    expect(await screen.findByText('Company sign-in')).toBeInTheDocument()
    expect(screen.getByText('You sign in with your company account: its password is changed there.')).toBeInTheDocument()
    expect(screen.getByText(/Your company account's sign-in asks for it/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Set up/ })).toBeNull()
    expect(screen.queryByLabelText('Current password')).toBeNull()
  })

  it('a new API key needs confirming, then is shown once', async () => {
    const calls = fakeApi(member, {
      'GET /api/account/keys': () => ({ json: { keys: [{ alias: 'mo', preview: 'sk-...abcd', spend: 1, blocked: false, createdAt: '2026-09-01T10:00:00Z' }], spend: 1, budget: 10 } }),
      'POST /api/account/keys/rotate': () => ({ json: { apiKey: 'sk-secret-new-key' } }),
    })
    renderApp('/account')
    expect(await screen.findByText('sk-...abcd')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'New key' }))
    const dialog = await screen.findByRole('alertdialog')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }))
    expect(calls.some((c) => c.path === '/api/account/keys/rotate')).toBe(false)

    await userEvent.click(screen.getByRole('button', { name: 'New key' }))
    await userEvent.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Make a new key' }))
    const secret = await screen.findByLabelText('API key')
    expect(secret).not.toHaveTextContent('sk-secret-new-key')
    await userEvent.click(screen.getByRole('button', { name: 'Show API key' }))
    expect(secret).toHaveTextContent('sk-secret-new-key')
    expect(screen.getByRole('meter', { name: 'Credit used' })).toHaveAttribute('aria-valuenow', '10')
  })

  it('the answer cache is each person’s choice for their key when an admin lets them choose', async () => {
    let chosen = false
    const calls = fakeApi(member, {
      'GET /api/account/answer-cache': () => ({ json: { mode: 'opt-in', chosen, on: chosen, ttlHours: 24 } }),
      'PUT /api/account/answer-cache': (body) => {
        chosen = (body as { on: boolean }).on
        return { json: { mode: 'opt-in', chosen, on: chosen, ttlHours: 24 } }
      },
    })
    renderApp('/account')
    const toggle = await screen.findByRole('switch', { name: 'Answer repeated requests from the cache' })
    expect(toggle).not.toBeChecked()
    expect(screen.getByText(/within 1 day, is answered from the cache/)).toBeInTheDocument()
    await userEvent.click(toggle)
    await waitFor(() => expect(calls.find((c) => c.method === 'PUT' && c.path === '/api/account/answer-cache')?.body).toEqual({ on: true }))
    await waitFor(() => expect(toggle).toBeChecked())
  })

  it('the answer cache on for every key is said, and off says nothing', async () => {
    fakeApi(member, { 'GET /api/account/answer-cache': () => ({ json: { mode: 'all', chosen: false, on: true, ttlHours: 6 } }) })
    const { unmount } = renderApp('/account')
    expect(await screen.findByText(/within 6 hours, is answered from the cache.*for every key/)).toBeInTheDocument()
    expect(screen.queryByRole('switch', { name: 'Answer repeated requests from the cache' })).not.toBeInTheDocument()
    unmount()

    fakeApi(member, { 'GET /api/account/answer-cache': () => ({ json: { mode: 'off', chosen: false, on: false, ttlHours: 24 } }) })
    renderApp('/account')
    expect(await screen.findByText('Credit used')).toBeInTheDocument()
    expect(screen.queryByText(/answered from the cache/)).not.toBeInTheDocument()
  })

  it('answers can be chosen short, normal or thorough', async () => {
    let length = 'normal'
    const calls = fakeApi(member, {
      'GET /api/account/preferences': () => ({ json: { answerLength: length } }),
      'PUT /api/account/preferences': (body) => {
        length = (body as { answerLength: string }).answerLength
        return { json: { answerLength: length } }
      },
    })
    renderApp('/account')
    const group = await screen.findByRole('radiogroup', { name: 'Answer length' })
    expect(within(group).getByRole('radio', { name: 'Normal' })).toBeChecked()
    await userEvent.click(within(group).getByRole('radio', { name: 'Short' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'PUT' && c.path === '/api/account/preferences')?.body).toEqual({ answerLength: 'short' }))
    await waitFor(() => expect(within(group).getByRole('radio', { name: 'Short' })).toBeChecked())
  })

  it('memory lists what the chat remembers, adds, edits, deletes and turns it off', async () => {
    let view = { enabled: true, on: true, max: 100, maxChars: 300, memories: [{ id: 'm1', text: 'Deploys with Podman.', createdAt: '2026-10-01T10:00:00Z', updatedAt: '2026-10-01T10:00:00Z' }] }
    const calls = fakeApi(member, {
      'GET /api/account/memories': () => ({ json: view }),
      'POST /api/account/memories': (body) => {
        const m = { id: 'm2', text: (body as { text: string }).text, createdAt: '2026-10-02T10:00:00Z', updatedAt: '2026-10-02T10:00:00Z' }
        view = { ...view, memories: [m, ...view.memories] }
        return { status: 201, json: m }
      },
      'PUT /api/account/memories/m1': (body) => {
        view = { ...view, memories: view.memories.map((m) => (m.id === 'm1' ? { ...m, text: (body as { text: string }).text } : m)) }
        return { json: view.memories.find((m) => m.id === 'm1') }
      },
      'DELETE /api/account/memories/m2': () => {
        view = { ...view, memories: view.memories.filter((m) => m.id !== 'm2') }
        return { status: 204 }
      },
      'PUT /api/account/memories/settings': (body) => {
        view = { ...view, on: (body as { on: boolean }).on }
        return { json: { on: view.on } }
      },
    })
    renderApp('/account')
    const card = await screen.findByRole('list', { name: 'Memories' })
    expect(within(card).getByText('Deploys with Podman.')).toBeInTheDocument()

    await userEvent.type(screen.getByRole('textbox', { name: 'Something to remember' }), 'Prefers Go examples.')
    await userEvent.click(screen.getByRole('button', { name: 'Add' }))
    expect(await within(card).findByText('Prefers Go examples.')).toBeInTheDocument()
    expect(calls.find((c) => c.method === 'POST' && c.path === '/api/account/memories')?.body).toEqual({ text: 'Prefers Go examples.' })

    await userEvent.click(within(card).getByRole('button', { name: 'Edit: Deploys with Podman.' }))
    const edit = within(card).getByRole('textbox', { name: 'Memory' })
    await userEvent.clear(edit)
    await userEvent.type(edit, 'Deploys with Podman 5.{Enter}')
    expect(await within(card).findByText('Deploys with Podman 5.')).toBeInTheDocument()

    await userEvent.click(within(card).getByRole('button', { name: 'Delete: Prefers Go examples.' }))
    await waitFor(() => expect(within(card).queryByText('Prefers Go examples.')).toBeNull())

    await userEvent.click(screen.getByRole('switch', { name: 'Use memory' }))
    await waitFor(() => expect(calls.find((c) => c.path === '/api/account/memories/settings')?.body).toEqual({ on: false }))
    await waitFor(() => expect(screen.getByRole('switch', { name: 'Use memory' })).not.toBeChecked())
  })

  it('the new passwords must match, and a change is sent once they do', async () => {
    const calls = fakeApi(member, { 'POST /api/account/password': () => ({ status: 204 }) })
    renderApp('/account')
    await userEvent.type(await screen.findByLabelText('Current password'), 'old-password')
    await userEvent.type(screen.getByLabelText('New password'), 'a brand new passphrase')
    await userEvent.type(screen.getByLabelText('New password again'), 'something else entirely')
    await userEvent.click(screen.getByRole('button', { name: 'Change password' }))
    expect(await screen.findByText('The new passwords differ.')).toBeInTheDocument()
    expect(calls.some((c) => c.path === '/api/account/password')).toBe(false)

    await userEvent.clear(screen.getByLabelText('New password again'))
    await userEvent.type(screen.getByLabelText('New password again'), 'a brand new passphrase')
    await userEvent.click(screen.getByRole('button', { name: 'Change password' }))
    await waitFor(() => expect(calls.find((c) => c.path === '/api/account/password')?.body).toEqual({ current: 'old-password', next: 'a brand new passphrase' }))
  })

  it('directory accounts change their password in the directory', async () => {
    fakeApi({ ...member, source: 'ldap' })
    renderApp('/account')
    expect(await screen.findByText(/managed by the company directory/)).toBeInTheDocument()
    expect(screen.queryByLabelText('Current password')).not.toBeInTheDocument()
  })

  it('turning on two-factor shows the key and then the recovery codes', async () => {
    const calls = fakeApi(member, {
      'POST /api/account/2fa/setup': () => ({ json: { sharedKey: 'JBSW Y3DP', uri: 'otpauth://totp/LLM:mo?secret=JBSWY3DP' } }),
      'POST /api/account/2fa/enable': () => ({ json: { recoveryCodes: ['aaaa-1111', 'bbbb-2222'] } }),
    })
    renderApp('/account')
    await userEvent.click(await screen.findByRole('button', { name: 'Set up' }))
    expect(await screen.findByText('JBSW Y3DP')).toBeInTheDocument()
    await userEvent.type(screen.getByLabelText('Code'), '123456')
    await userEvent.click(screen.getByRole('button', { name: 'Turn on' }))
    expect(await screen.findByText('aaaa-1111')).toBeInTheDocument()
    expect(calls.find((c) => c.path === '/api/account/2fa/enable')?.body).toEqual({ code: '123456' })
  })

  it('the theme choice applies at once and is remembered', async () => {
    fakeApi(member)
    renderApp('/account')
    await userEvent.click(await screen.findByRole('radio', { name: 'Dark' }))
    expect(document.documentElement).toHaveClass('dark')
    expect(localStorage.getItem('theme')).toBe('dark')
  })
})
