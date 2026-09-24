import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { admin, fakeApi, renderApp } from '@/test/utils'
import type { SettingsData, SettingView } from './settings-model'

const s = (over: Partial<SettingView>): SettingView => ({
  key: 'Chat:MaxToolRounds', group: 'Chat', label: 'Tool calls per answer', help: 'Rounds of tool use.', type: 'wholenumber', scope: 'live',
  options: null, min: 1, max: 32, patternHelp: null, unit: null, optional: false, impact: null, dangerous: false, default: '8',
  value: '8', isSet: true, source: 'default', environmentValue: null, pending: null, pendingSet: false, restartPending: false, ...over,
})

function data(over: Partial<SettingsData> = {}): SettingsData {
  return {
    groups: [
      {
        title: 'Chat',
        settings: [
          s({}),
          s({ key: 'Chat:RequestTimeout', label: 'Longest single answer', type: 'duration', scope: 'apprestart', unit: 'minutes', value: '00:15:00', default: '00:15:00' }),
        ],
      },
      {
        title: 'Company directory (LDAP)',
        settings: [
          s({ key: 'Ldap:Url', group: 'Company directory (LDAP)', label: 'Directory server', type: 'url', optional: true, value: '', default: null, source: 'default' }),
          s({ key: 'Ldap:BindPassword', group: 'Company directory (LDAP)', label: 'Service account password', type: 'secret', optional: true, value: null, isSet: true, default: null, source: 'saved' }),
        ],
      },
      {
        title: 'Model',
        settings: [
          s({ key: 'MODEL_CONTEXT', group: 'Model', label: 'Context window', scope: 'stack', value: '131072', source: 'stack', default: null, min: 1024, max: 4194304 }),
          s({ key: 'LLAMACPP_EXTRA_ARGS', group: 'Model', label: 'Extra engine flags', type: 'text', scope: 'stack', value: '--flash-attn on', source: 'stack', dangerous: true, default: null, optional: true }),
        ],
      },
    ],
    pendingStack: 0,
    restartNeeded: false,
    pendingFileWritable: true,
    ...over,
  }
}

describe('settings', () => {
  it('saves only what changed, with durations in the API’s form, and says when each applies', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/config': () => ({ json: data() }),
      'PUT /api/admin/config': () => ({ json: data({ restartNeeded: true }) }),
    })
    renderApp('/admin/settings')
    const rounds = await screen.findByLabelText('Tool calls per answer')
    await userEvent.clear(rounds)
    await userEvent.type(rounds, '3')
    const timeout = screen.getByLabelText('Longest single answer')
    await userEvent.clear(timeout)
    await userEvent.type(timeout, '90')
    const bar = screen.getByRole('region', { name: 'Unsaved changes' })
    expect(bar).toHaveTextContent('2 unsaved changes')
    await userEvent.click(within(bar).getByRole('button', { name: 'Save changes' }))
    await waitFor(() =>
      expect(calls.find((c) => c.method === 'PUT')?.body).toEqual({
        changes: [
          { key: 'Chat:MaxToolRounds', value: '3' },
          { key: 'Chat:RequestTimeout', value: '01:30:00' },
        ],
      }),
    )
    expect(await screen.findByText('1 in effect now · 1 after a restart')).toBeInTheDocument()
    expect(await screen.findByText('Some changes apply when the app restarts')).toBeInTheDocument()
    expect(screen.queryByRole('region', { name: 'Unsaved changes' })).not.toBeInTheDocument()
  })

  it('shows the server’s reason next to the field', async () => {
    fakeApi(admin, {
      'GET /api/admin/config': () => ({ json: data() }),
      'PUT /api/admin/config': () => ({ status: 400, json: { status: 'invalid', error: 'Some settings are not valid.', errors: { 'Chat:MaxToolRounds': 'At most 32.' } } }),
    })
    renderApp('/admin/settings')
    const rounds = await screen.findByLabelText('Tool calls per answer')
    await userEvent.clear(rounds)
    await userEvent.type(rounds, '99')
    await userEvent.click(screen.getByRole('button', { name: 'Save changes' }))
    expect(await screen.findByText('At most 32.')).toBeInTheDocument()
    expect(rounds).toHaveAttribute('aria-invalid', 'true')
  })

  it('a dangerous change asks first', async () => {
    const calls = fakeApi(admin, { 'GET /api/admin/config': () => ({ json: data() }), 'PUT /api/admin/config': () => ({ json: data() }) })
    renderApp('/admin/settings#model')
    await userEvent.type(await screen.findByLabelText('Extra engine flags'), ' -b 2048')
    await userEvent.click(screen.getByRole('button', { name: 'Save changes' }))
    const ask = await screen.findByRole('alertdialog')
    expect(ask).toHaveTextContent('Extra engine flags')
    await userEvent.click(within(ask).getByRole('button', { name: 'Cancel' }))
    expect(calls.some((c) => c.method === 'PUT')).toBe(false)
  })

  it('a secret is never shown, and only a typed value is sent', async () => {
    const calls = fakeApi(admin, { 'GET /api/admin/config': () => ({ json: data() }), 'PUT /api/admin/config': () => ({ json: data() }) })
    renderApp('/admin/settings#company-directory-ldap')
    const secret = await screen.findByLabelText('Service account password')
    expect(secret).toHaveValue('')
    expect(secret).toHaveAttribute('type', 'password')
    expect(screen.getByText('Set. Type a new value to replace it.')).toBeInTheDocument()
    await userEvent.type(secret, 'new-pass')
    await userEvent.click(screen.getByRole('button', { name: 'Save changes' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'PUT')?.body).toEqual({ changes: [{ key: 'Ldap:BindPassword', value: 'new-pass' }] }))
  })

  it('a saved setting goes back to its default in one click', async () => {
    const calls = fakeApi(admin, { 'GET /api/admin/config': () => ({ json: data() }), 'PUT /api/admin/config': () => ({ json: data() }) })
    renderApp('/admin/settings#company-directory-ldap')
    await userEvent.click(await screen.findByRole('button', { name: /Back to the default/ }))
    await waitFor(() => expect(calls.find((c) => c.method === 'PUT')?.body).toEqual({ changes: [{ key: 'Ldap:BindPassword', reset: true }] }))
  })

  it('pending .env changes show the one command that applies them', async () => {
    const d = data({ pendingStack: 1 })
    d.groups[2]!.settings[0] = { ...d.groups[2]!.settings[0]!, pending: '65536', pendingSet: true }
    fakeApi(admin, { 'GET /api/admin/config': () => ({ json: d }) })
    renderApp('/admin/settings#model')
    expect(await screen.findByText('1 .env change is waiting to be applied')).toBeInTheDocument()
    expect(screen.getByLabelText('apply command')).toHaveTextContent('./scripts/apply-settings.sh')
    expect(screen.getByLabelText('Context window')).toHaveValue('65536')
    expect(screen.getByText(/Now:/)).toHaveTextContent('131072')
    expect(screen.getByRole('button', { name: /Discard pending change/ })).toBeInTheDocument()
  })

  it('shows one group at a time, and a search spans them all', async () => {
    fakeApi(admin, { 'GET /api/admin/config': () => ({ json: data() }) })
    renderApp('/admin/settings')
    expect(await screen.findByLabelText('Tool calls per answer')).toBeInTheDocument()
    expect(screen.queryByLabelText('Context window')).not.toBeInTheDocument()
    await userEvent.click(within(screen.getByRole('navigation', { name: 'Setting groups' })).getByRole('link', { name: /^Model/ }))
    expect(await screen.findByLabelText('Context window')).toBeInTheDocument()
    expect(screen.queryByLabelText('Tool calls per answer')).not.toBeInTheDocument()
    await userEvent.type(screen.getByRole('searchbox', { name: 'Search settings' }), 'answer')
    expect(screen.getByLabelText('Tool calls per answer')).toBeInTheDocument()
    expect(screen.getByLabelText('Longest single answer')).toBeInTheDocument()
    expect(screen.queryByLabelText('Context window')).not.toBeInTheDocument()
  })

  it('unsaved changes survive moving between groups', async () => {
    const calls = fakeApi(admin, { 'GET /api/admin/config': () => ({ json: data() }), 'PUT /api/admin/config': () => ({ json: data() }) })
    renderApp('/admin/settings')
    const rounds = await screen.findByLabelText('Tool calls per answer')
    await userEvent.clear(rounds)
    await userEvent.type(rounds, '4')
    await userEvent.click(within(screen.getByRole('navigation', { name: 'Setting groups' })).getByRole('link', { name: /^Model/ }))
    const context = await screen.findByLabelText('Context window')
    await userEvent.clear(context)
    await userEvent.type(context, '65536')
    await userEvent.click(within(screen.getByRole('region', { name: 'Unsaved changes' })).getByRole('button', { name: 'Save changes' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'PUT')?.body).toMatchObject({ changes: [{}, {}] }))
  })

  it('the directory test sends the unsaved values and shows the answer', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/config': () => ({ json: data() }),
      'POST /api/admin/config/ldap-test': () => ({ json: { ok: false, message: 'Could not reach ldap://dc1:389' } }),
    })
    renderApp('/admin/settings#company-directory-ldap')
    await userEvent.type(await screen.findByLabelText('Directory server'), 'ldap://dc1:389')
    await userEvent.click(screen.getByRole('button', { name: 'Test connection' }))
    expect(await screen.findByText('Could not reach ldap://dc1:389')).toBeInTheDocument()
    expect(calls.find((c) => c.path === '/api/admin/config/ldap-test')?.body).toEqual({ 'Ldap:Url': 'ldap://dc1:389' })
  })

  it('the app restarts itself and the page waits for it', async () => {
    let restarted = false
    fakeApi(admin, {
      'GET /api/admin/config': () => ({ json: data({ restartNeeded: !restarted }) }),
      'POST /api/admin/config/restart': () => {
        restarted = true
        return { status: 202, json: { status: 'restarting' } }
      },
    })
    renderApp('/admin/settings')
    await userEvent.click(await screen.findByRole('button', { name: 'Restart the app now' }))
    await waitFor(() => expect(screen.queryByText('Some changes apply when the app restarts')).not.toBeInTheDocument(), { timeout: 6000 })
  }, 10_000)
})
