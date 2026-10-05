import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { admin, fakeApi, member, renderApp } from '@/test/utils'

describe('admin plugins', () => {
  it('installs one from the catalog with its settings, after saying what it will do', async () => {
    let installed = false
    const calls = fakeApi(admin, {
      'GET /api/admin/plugins': () => ({
        json: {
          problem: null,
          catalog: [{ name: 'gitlab-issues', version: '1.0.0', title: 'GitLab issues', description: 'Issues, as you.', source: 'app', personAuth: 'oauth2', installed: installed ? { id: 's1', version: '1.0.0' } : null }],
          installed: [],
        },
      }),
      'POST /api/admin/plugins/preview': () => ({
        json: {
          name: 'gitlab-issues', version: '1.0.0', title: 'GitLab issues', description: 'Issues, as you.', personAuth: 'oauth2', help: 'Sign in to GitLab.', writes: ['create_issue', 'add_note'], operations: 7, mcp: null,
          prompts: [{ name: 'triage', title: 'Triage a GitLab issue' }],
          settings: [
            { key: 'gitlab_url', title: 'GitLab address', type: 'url', required: true, help: null },
            { key: 'client_secret', title: 'Application secret', type: 'secret', required: true, help: null },
          ],
        },
      }),
      'POST /api/admin/plugins/install': () => {
        installed = true
        return { status: 201, json: { id: 's1', toolId: 'mcp:s1' } }
      },
    })
    renderApp('/admin/plugins')
    const card = await screen.findByRole('region', { name: 'GitLab issues' })
    expect(within(card).getByText('Each person signs in')).toBeInTheDocument()
    await userEvent.click(within(card).getByRole('button', { name: /Install/ }))
    const dialog = await screen.findByRole('dialog', { name: 'Install GitLab issues 1.0.0' })
    expect(within(dialog).getByText('7 operations of its API.')).toBeInTheDocument()
    expect(within(dialog).getByText('Asks the person first before: create_issue, add_note.')).toBeInTheDocument()
    expect(within(dialog).getByText('Adds prompts for whoever may use it: /triage (Triage a GitLab issue).')).toBeInTheDocument()
    await userEvent.type(within(dialog).getByLabelText('GitLab address'), 'https://gitlab.example.test')
    await userEvent.type(within(dialog).getByLabelText('Application secret'), 's3cret')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Install' }))
    await waitFor(() =>
      expect(calls.find((c) => c.path === '/api/admin/plugins/install')?.body).toEqual({
        name: 'gitlab-issues', settings: { gitlab_url: 'https://gitlab.example.test', client_secret: 's3cret' }, tls: 'System', tlsCa: null,
      }),
    )
    expect(await within(await screen.findByRole('region', { name: 'GitLab issues' })).findByText('Installed 1.0.0')).toBeInTheDocument()
  })

  it("its server's certificate check is in its settings, and not checking is warned of", async () => {
    const installed = {
      id: 's1', toolId: 'mcp:s1', plugin: 'gitlab-issues', title: 'GitLab issues', version: '1.0.0', url: 'https://gitlab.corp.test/api/v4', personAuth: 'oauth2', writes: [],
      settings: [{ key: 'gitlab_url', title: 'GitLab address', type: 'url', required: true, help: null, value: 'https://gitlab.corp.test', set: true }],
      tls: 'OwnCa', tlsCa: '-----BEGIN CERTIFICATE-----\nMIIB\n-----END CERTIFICATE-----', tlsCaNames: ['Corp CA'],
    }
    const calls = fakeApi(admin, {
      'GET /api/admin/plugins': () => ({ json: { problem: null, catalog: [], installed: [installed] } }),
      'PATCH /api/admin/plugins/s1': () => ({ status: 204 }),
    })
    renderApp('/admin/plugins')
    const card = await screen.findByRole('region', { name: 'GitLab issues' })
    expect(within(card).getByText('Corp CA')).toBeInTheDocument()
    await userEvent.click(within(card).getByRole('button', { name: /Settings/ }))
    const dialog = await screen.findByRole('dialog', { name: 'GitLab issues: settings' })
    expect(within(dialog).getByRole('radio', { name: /Trust this CA/ })).toBeChecked()
    expect(within(dialog).getByLabelText('CA certificate (PEM)')).toHaveValue(installed.tlsCa)
    await userEvent.click(within(dialog).getByRole('radio', { name: /Do not check/ }))
    expect(within(dialog).getByText('Its certificate will not be checked')).toBeInTheDocument()
    expect(within(dialog).queryByLabelText('CA certificate (PEM)')).not.toBeInTheDocument()
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'PATCH')?.body).toEqual({ settings: {}, tls: 'Off', tlsCa: null }))
  })

  it('an installed plugin whose certificate is not checked says so on its card', async () => {
    fakeApi(admin, {
      'GET /api/admin/plugins': () => ({
        json: {
          problem: null, catalog: [],
          installed: [{ id: 's1', toolId: 'mcp:s1', plugin: 'lab', title: 'Lab', version: '1', url: 'https://lab.test/mcp', personAuth: null, writes: [], settings: [], tls: 'Off', tlsCa: null, tlsCaNames: [] }],
        },
      }),
    })
    renderApp('/admin/plugins')
    expect(await within(await screen.findByRole('region', { name: 'Lab' })).findByText('Its certificate is not checked')).toBeInTheDocument()
  })
})

describe('account connections', () => {
  it('connects with a key, or sends the person to sign in', async () => {
    let connected = false
    const calls = fakeApi(member, {
      'GET /api/account/connections': () => ({
        json: [
          { toolId: 'mcp:a', title: 'Pets', kind: 'api_key', help: 'Your pet store key.', connected, account: null, since: connected ? '2026-10-04T00:00:00Z' : null },
          { toolId: 'mcp:b', title: 'GitLab issues', kind: 'oauth2', help: null, connected: false, account: null, since: null },
        ],
      }),
      'PUT /api/account/connections/mcp%3Aa': () => {
        connected = true
        return { status: 204 }
      },
    })
    renderApp('/account')
    const pets = await screen.findByRole('listitem', { name: 'Pets' })
    await userEvent.type(within(pets).getByLabelText('Pets key'), 'my-key')
    await userEvent.click(within(pets).getByRole('button', { name: 'Save' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'PUT')?.body).toEqual({ secret: 'my-key' }))
    expect(await within(await screen.findByRole('listitem', { name: 'Pets' })).findByText('Connected')).toBeInTheDocument()
    expect(within(screen.getByRole('listitem', { name: 'GitLab issues' })).getByRole('link', { name: /Connect/ })).toHaveAttribute('href', '/api/account/connections/mcp%3Ab/connect')
  })
})
