import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { admin, fakeApi, renderApp } from '@/test/utils'
import type { SettingsData, SettingView } from './settings-model'

const s = (over: Partial<SettingView>): SettingView => ({
  key: 'Chat:MaxToolRounds', group: 'Chat', label: 'Tool calls per answer', help: 'Rounds of tool use.', type: 'wholenumber', scope: 'live',
  options: null, min: 1, max: 32, patternHelp: null, unit: null, optional: false, impact: null, dangerous: false, default: '8',
  value: '8', isSet: true, source: 'default', environmentValue: null, restartPending: false, ...over,
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
          s({ key: 'Engine:ModelsMax', group: 'Model', label: 'Models loaded at once', scope: 'apprestart', value: '1', default: '1', min: 1, max: 8 }),
          s({ key: 'Chat:ThinkingPresets', group: 'Model', label: 'Thinking levels offered', type: 'text', value: 'medium:Balanced', dangerous: true, default: null, optional: true }),
        ],
      },
    ],
    restartNeeded: false,
    ...over,
  }
}

describe('settings', () => {
  it('explains every setting in plain view, not only on hover: what it does, when it applies, and what is risky', async () => {
    fakeApi(admin, { 'GET /api/admin/config': () => ({ json: data() }) })
    renderApp('/admin/settings#model')
    const models = await screen.findByLabelText('Models loaded at once')
    const levels = screen.getByLabelText('Thinking levels offered')
    // Each field's explanation is on the page and is its description, for screen readers too.
    expect(models).toHaveAccessibleDescription('Rounds of tool use. Read when the app starts: save, then restart the app (a few seconds).')
    expect(levels).toHaveAccessibleDescription('Rounds of tool use. Careful: a wrong value can stop a service from starting.')
    const card = screen.getByRole('region', { name: 'Model' })
    expect(within(card).getByText('After a restart')).toBeVisible()
    expect(within(card).getByText('Applies at once')).toBeVisible()
    // Nothing left that says more only on hover.
    expect(within(card).queryAllByRole('button')).toEqual([])
  })

  it('shows each number and duration with its unit and limits, and a changed setting with its default', async () => {
    const d = data()
    const chat = d.groups.find((g) => g.title === 'Chat')!
    chat.settings.push(
      s({ key: 'Auth:IdleTimeout', label: 'Sign out after idle', type: 'duration', scope: 'apprestart', unit: 'minutes', min: 5, max: 1440, value: '02:00:00', default: '01:00:00', source: 'saved' }),
      s({ key: 'Chat:MaxPasteChars', label: 'Longest paste', unit: 'characters', min: 0, max: 500000, value: '9000', default: '6000', source: 'environment' }),
    )
    fakeApi(admin, { 'GET /api/admin/config': () => ({ json: d }) })
    renderApp('/admin/settings')
    const idle = await screen.findByLabelText('Sign out after idle')
    expect(idle).toHaveValue('120')
    const row = (field: HTMLElement) => field.closest('.grid.content-start') as HTMLElement
    expect(row(idle)).toHaveTextContent('minutes5–1440')
    expect(within(row(idle)).getByText('60 minutes')).toBeInTheDocument()
    expect(row(idle)).toHaveTextContent('The default: 60 minutes.')
    const paste = screen.getByLabelText('Longest paste')
    expect(row(paste)).toHaveTextContent('characters0–500000')
    expect(row(paste)).toHaveTextContent('Set by the environment. The default: 6000 characters.')
    // One left at its default says only that.
    expect(row(screen.getByLabelText('Tool calls per answer'))).toHaveTextContent('1–32The default.')
  })

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
    await userEvent.type(await screen.findByLabelText('Thinking levels offered'), ',low:Quick')
    await userEvent.click(screen.getByRole('button', { name: 'Save changes' }))
    const ask = await screen.findByRole('alertdialog')
    expect(ask).toHaveTextContent('Thinking levels offered')
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

  it('shows one group at a time, and a search spans them all', async () => {
    fakeApi(admin, { 'GET /api/admin/config': () => ({ json: data() }) })
    renderApp('/admin/settings')
    expect(await screen.findByLabelText('Tool calls per answer')).toBeInTheDocument()
    expect(screen.queryByLabelText('Models loaded at once')).not.toBeInTheDocument()
    await userEvent.click(within(screen.getByRole('navigation', { name: 'Setting groups' })).getByRole('link', { name: /^Model/ }))
    expect(await screen.findByLabelText('Models loaded at once')).toBeInTheDocument()
    expect(screen.queryByLabelText('Tool calls per answer')).not.toBeInTheDocument()
    await userEvent.type(screen.getByRole('searchbox', { name: 'Search settings' }), 'answer')
    expect(screen.getByLabelText('Tool calls per answer')).toBeInTheDocument()
    expect(screen.getByLabelText('Longest single answer')).toBeInTheDocument()
    expect(screen.queryByLabelText('Models loaded at once')).not.toBeInTheDocument()
  })

  it('unsaved changes survive moving between groups', async () => {
    const calls = fakeApi(admin, { 'GET /api/admin/config': () => ({ json: data() }), 'PUT /api/admin/config': () => ({ json: data() }) })
    renderApp('/admin/settings')
    const rounds = await screen.findByLabelText('Tool calls per answer')
    await userEvent.clear(rounds)
    await userEvent.type(rounds, '4')
    await userEvent.click(within(screen.getByRole('navigation', { name: 'Setting groups' })).getByRole('link', { name: /^Model/ }))
    const context = await screen.findByLabelText('Models loaded at once')
    await userEvent.clear(context)
    await userEvent.type(context, '2')
    await userEvent.click(within(screen.getByRole('region', { name: 'Unsaved changes' })).getByRole('button', { name: 'Save changes' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'PUT')?.body).toMatchObject({ changes: [{}, {}] }))
  })

  it('the directory test sends the unsaved values and shows the answer and each step', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/config': () => ({ json: data() }),
      'POST /api/admin/config/ldap-test': () => ({
        json: {
          ok: false,
          message: 'The server refused the service account cn=reader,dc=example,dc=com with the saved password: the DN or the password is wrong.',
          steps: [
            { state: 'ok', text: 'Reached dc1:389.' },
            { state: 'warn', text: 'Not encrypted.' },
            { state: 'fail', text: 'The server refused the service account cn=reader,dc=example,dc=com with the saved password: the DN or the password is wrong.' },
          ],
        },
      }),
    })
    renderApp('/admin/settings#company-directory-ldap')
    await userEvent.type(await screen.findByLabelText('Directory server'), 'ldap://dc1:389')
    await userEvent.click(screen.getByRole('button', { name: 'Test the settings' }))
    // What failed is said once, first; then the steps that led to it.
    const steps = await screen.findByRole('list', { name: 'Steps before it' })
    expect(within(steps).getAllByRole('listitem').map((li) => li.textContent)).toEqual(['Done: Reached dc1:389.', 'Warning: Not encrypted.'])
    expect(screen.getByRole('alert')).toHaveTextContent('the DN or the password is wrong')
    // Its DNs and filters are long words: the answer wraps them, as the steps do, rather than widen a phone's page.
    expect(screen.getByRole('alert')).toHaveClass('[&_p]:break-words')
    expect(screen.getAllByText(/the DN or the password is wrong/)).toHaveLength(1)
    expect(calls.find((c) => c.path === '/api/admin/config/ldap-test')?.body).toEqual({ 'Ldap:Url': 'ldap://dc1:389' })
  })

  it('a person’s sign-in is tried with the unsaved values, and the password goes only to that check', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/config': () => ({ json: data() }),
      'POST /api/admin/config/ldap-try': () => ({
        json: { ok: true, message: 'jsmith can sign in, as an admin.', steps: [{ state: 'ok', text: 'Found uid=jsmith,ou=people,dc=example,dc=com.' }, { state: 'ok', text: 'Their password is right.' }] },
      }),
    })
    renderApp('/admin/settings#company-directory-ldap')
    await userEvent.type(await screen.findByLabelText('Directory server'), 'ldap://dc1:389')
    const tryIt = screen.getByRole('button', { name: 'Try it' })
    expect(tryIt).toBeDisabled()
    await userEvent.type(screen.getByLabelText('Their username'), 'CORP\\jsmith')
    await userEvent.type(screen.getByLabelText('Their password'), 'their pw')
    await userEvent.click(tryIt)
    expect(await screen.findByText('jsmith can sign in, as an admin.')).toBeInTheDocument()
    expect(screen.getByText('Their password is right.')).toBeInTheDocument()
    expect(calls.find((c) => c.path === '/api/admin/config/ldap-try')?.body).toEqual({ settings: { 'Ldap:Url': 'ldap://dc1:389' }, login: 'CORP\\jsmith', password: 'their pw' })
    // It is not a setting: nothing to save.
    expect(screen.queryByRole('region', { name: 'Unsaved changes' })).toHaveTextContent('1 unsaved change')
  })

  it('the setup guide is open while no directory is set, with an example for OpenLDAP and Active Directory', async () => {
    fakeApi(admin, { 'GET /api/admin/config': () => ({ json: data() }) })
    renderApp('/admin/settings#company-directory-ldap')
    const guide = (await screen.findByText('How to set it up, step by step')).closest('details')!
    expect(guide).toHaveAttribute('open')
    const examples = within(guide).getByRole('table', { name: 'An example of each field' })
    expect(within(examples).getByRole('row', { name: /Service account/ })).toHaveTextContent('cn=readonly,dc=example,dc=com')
    expect(within(examples).getByRole('row', { name: /Service account/ })).toHaveTextContent('reader@corp.example.com')
    // The osixia/openldap image's own accounts, and the TLS setting without which its StartTLS and ldaps:// refuse the app.
    expect(guide).toHaveTextContent('cn=readonly,dc=example,dc=org')
    expect(guide).toHaveTextContent('LDAP_TLS_VERIFY_CLIENT=try')
  })

  it('each example for OpenLDAP and Active Directory in a setting\'s help is on a line of its own', async () => {
    const set = data()
    set.groups[1].settings[0] = {
      ...set.groups[1].settings[0],
      help: 'Where the directory listens. OpenLDAP: ldaps://ldap.example.com:636. Active Directory: ldaps://dc1.corp.example.com:636, a domain controller.',
    }
    fakeApi(admin, { 'GET /api/admin/config': () => ({ json: set }) })
    renderApp('/admin/settings#company-directory-ldap')
    const help = (await screen.findByText('Where the directory listens.', { exact: false })).closest('p')!
    const lines = [...help.querySelectorAll(':scope > span.block')].map((l) => l.textContent)
    expect(lines).toEqual(['OpenLDAP: ldaps://ldap.example.com:636.', 'Active Directory: ldaps://dc1.corp.example.com:636, a domain controller.'])
    expect(screen.getByLabelText('Directory server')).toHaveAccessibleDescription(
      'Where the directory listens. OpenLDAP: ldaps://ldap.example.com:636. Active Directory: ldaps://dc1.corp.example.com:636, a domain controller.',
    )
  })

  it('the setup guide starts closed once a directory is set', async () => {
    const set = data()
    set.groups[1].settings[0] = { ...set.groups[1].settings[0], value: 'ldaps://dc1.corp.example.com:636', source: 'saved' }
    fakeApi(admin, { 'GET /api/admin/config': () => ({ json: set }) })
    renderApp('/admin/settings#company-directory-ldap')
    expect((await screen.findByText('How to set it up, step by step')).closest('details')).not.toHaveAttribute('open')
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

  it('says under a setting what is wrong with it for the rest of the deployment', async () => {
    const d = data()
    const model = d.groups.find((g) => g.title === 'Model')!
    model.settings.push(
      s({
        key: 'Chat:SmallModel', group: 'Model', label: 'Model for sub-agents and small steps', type: 'text', optional: true, value: 'Small-Model', default: null, source: 'saved',
        warning: 'Small-Model is not kept loaded: each small step waits for it to load, and pushes another model out.',
      }),
    )
    fakeApi(admin, { 'GET /api/admin/config': () => ({ json: d }) })
    renderApp('/admin/settings#model')
    expect(await screen.findByLabelText('Model for sub-agents and small steps')).toHaveValue('Small-Model')
    expect(screen.getByText(/Small-Model is not kept loaded/)).toBeInTheDocument()
    expect(screen.getAllByText(/is not kept loaded/)).toHaveLength(1)
  })
})
