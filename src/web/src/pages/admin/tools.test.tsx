import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { admin, fakeApi, renderApp } from '@/test/utils'

const tool = (id: string, title: string, over: object = {}) => ({
  id, title, description: `${title} does things.`, icon: 'calculator', unavailable: null,
  setting: { enabled: true, audience: 'Everyone', onByDefault: true, askFirst: false, groups: [] }, server: null, ...over,
})

describe('admin tools', () => {
  it('a tool is turned off, given to a group, and set to ask first', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/tools': () => ({ json: [tool('calculator', 'Calculator'), tool('image', 'Image generation', { unavailable: 'The gateway serves no image model.' })] }),
      'PUT /api/admin/tools/calculator': () => ({ status: 204 }),
      'GET /api/admin/groups': () => ({ json: [{ id: 'g1', name: 'Finance', description: null, directory: null, members: 3, createdAt: '' }] }),
    })
    renderApp('/admin/tools')
    const card = (await screen.findByRole('heading', { name: /Calculator/ })).closest('section')!
    expect(screen.getByText('The gateway serves no image model.')).toBeInTheDocument()
    await userEvent.click(within(card).getByRole('switch', { name: 'Calculator on' }))
    await waitFor(() => expect(calls.filter((c) => c.method === 'PUT').at(-1)?.body).toMatchObject({ enabled: false }))

    // "Chosen groups" is only saved once a group is chosen.
    await userEvent.click(within(card).getByRole('combobox', { name: 'Who may use it' }))
    await userEvent.click(await screen.findByRole('option', { name: 'Chosen groups' }))
    expect(calls.filter((c) => c.method === 'PUT')).toHaveLength(1)
    await userEvent.click(within(card).getByRole('button', { name: 'Choose groups' }))
    await userEvent.click(await screen.findByRole('checkbox', { name: /Finance/ }))
    await waitFor(() => expect(calls.filter((c) => c.method === 'PUT').at(-1)?.body).toMatchObject({ audience: 'Groups', groups: ['g1'] }))

    await userEvent.click(within(card).getByRole('switch', { name: /Ask before each call/ }))
    await waitFor(() => expect(calls.filter((c) => c.method === 'PUT').at(-1)?.body).toMatchObject({ askFirst: true }))
  })

  it('an MCP server is tested, then added', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/tools': () => ({ json: [] }),
      'POST /api/admin/tools/servers/test': () => ({ json: { ok: true, tools: [{ name: 'echo', description: 'Says it back' }] } }),
      'POST /api/admin/tools/servers': () => ({ status: 201, json: { id: 's1', toolId: 'mcp:s1' } }),
    })
    renderApp('/admin/tools')
    await userEvent.click(await screen.findByRole('button', { name: 'Add a server or API' }))
    const dialog = await screen.findByRole('dialog', { name: 'Add a server or API' })
    await userEvent.type(within(dialog).getByLabelText('Name'), 'Echo desk')
    await userEvent.type(within(dialog).getByLabelText('Address'), 'https://tools.example.test/mcp')
    await userEvent.type(within(dialog).getByLabelText('Header name'), 'X-Api-Key')
    await userEvent.type(within(dialog).getByLabelText('Header value'), 'secret')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Test' }))
    expect(await within(dialog).findByText('Connected: 1 tools')).toBeInTheDocument()
    expect(within(dialog).getByText('echo')).toBeInTheDocument()
    await userEvent.click(within(dialog).getByRole('button', { name: 'Add server' }))
    await waitFor(() =>
      expect(calls.find((c) => c.method === 'POST' && c.path === '/api/admin/tools/servers')?.body).toMatchObject({ name: 'Echo desk', url: 'https://tools.example.test/mcp', headerName: 'X-Api-Key', headerValue: 'secret' }),
    )
  })

  it('a certificate the test refuses says why, and its CA is trusted from a file', async () => {
    const pem = '-----BEGIN CERTIFICATE-----\nMIIBcorp\n-----END CERTIFICATE-----'
    const calls = fakeApi(admin, {
      'GET /api/admin/tools': () => ({ json: [] }),
      'POST /api/admin/tools/servers/test': (body) =>
        (body as { tls: string }).tls === 'OwnCa'
          ? { json: { ok: true, tools: [{ name: 'echo', description: null }] } }
          : {
              json: {
                ok: false,
                error: "Corp desk's certificate is not trusted.",
                certificate: {
                  subject: 'CN=desk.corp.test', issuer: 'CN=Corp CA, O=Corp', names: ['desk.corp.test'], notBefore: '2026-01-01T00:00:00Z', notAfter: '2027-01-01T00:00:00Z',
                  selfSigned: false, sha256: 'AB:CD', reasons: ['It was issued by Corp CA, a CA this server does not trust.'],
                },
              },
            },
      'POST /api/admin/tools/servers': () => ({ status: 201, json: { id: 's3', toolId: 'mcp:s3' } }),
    })
    renderApp('/admin/tools')
    await userEvent.click(await screen.findByRole('button', { name: 'Add a server or API' }))
    const dialog = await screen.findByRole('dialog', { name: 'Add a server or API' })
    await userEvent.type(within(dialog).getByLabelText('Name'), 'Corp desk')
    await userEvent.type(within(dialog).getByLabelText('Address'), 'https://desk.corp.test/mcp')
    expect(within(dialog).getByRole('radio', { name: /Check the certificate/ })).toBeChecked()
    await userEvent.click(within(dialog).getByRole('button', { name: 'Test' }))

    // Who issued it and why, with the two ways on.
    const refused = await within(dialog).findByRole('alert')
    expect(within(refused).getByText('Its certificate is not trusted')).toBeInTheDocument()
    expect(within(refused).getByText('It was issued by Corp CA, a CA this server does not trust.')).toBeInTheDocument()
    expect(within(refused).getByText('CN=Corp CA, O=Corp')).toBeInTheDocument()
    expect(within(refused).getByText('AB:CD')).toBeInTheDocument()
    await userEvent.click(within(refused).getByRole('button', { name: 'Trust its CA' }))
    expect(within(dialog).getByRole('radio', { name: /Trust this CA/ })).toBeChecked()

    // The file fills the PEM; the test and the save send it.
    await userEvent.upload(within(dialog).getByLabelText('CA certificate file'), new File([pem], 'corp-ca.pem', { type: 'application/x-pem-file' }))
    await waitFor(() => expect(within(dialog).getByLabelText('CA certificate (PEM)')).toHaveValue(pem))
    await userEvent.click(within(dialog).getByRole('button', { name: 'Test' }))
    expect(await within(dialog).findByText('Connected: 1 tools')).toBeInTheDocument()
    expect(calls.filter((c) => c.path === '/api/admin/tools/servers/test').at(-1)?.body).toMatchObject({ tls: 'OwnCa', tlsCa: pem })
    await userEvent.click(within(dialog).getByRole('button', { name: 'Add server' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'POST' && c.path === '/api/admin/tools/servers')?.body).toMatchObject({ name: 'Corp desk', tls: 'OwnCa', tlsCa: pem }))
  })

  it('a server not checked is warned of on its card and in its form', async () => {
    const server = {
      id: 's4', name: 'Lab desk', description: null, url: 'https://lab.test/mcp', headerName: null, headerSet: false, emailHeader: null, callTimeoutMinutes: null,
      prefix: 'lab_desk__', kind: 'mcp', spec: null, tls: 'Off', tlsCa: null, tlsCaNames: [],
    }
    const corp = { ...server, id: 's5', name: 'Corp desk', url: 'https://corp.test/mcp', prefix: 'corp_desk__', tls: 'OwnCa', tlsCa: 'PEM', tlsCaNames: ['Corp CA'] }
    const calls = fakeApi(admin, {
      'GET /api/admin/tools': () => ({ json: [tool('mcp:s4', 'Lab desk', { server }), tool('mcp:s5', 'Corp desk', { server: corp })] }),
      'PATCH /api/admin/tools/servers/s4': () => ({ status: 204 }),
    })
    renderApp('/admin/tools')
    const card = (await screen.findByRole('heading', { name: /Lab desk/ })).closest('section')!
    expect(within(card).getByText('Its certificate is not checked')).toBeInTheDocument()
    const corpCard = screen.getByRole('heading', { name: /Corp desk/ }).closest('section')!
    expect(within(corpCard).getByText('Corp CA')).toBeInTheDocument()
    expect(within(corpCard).queryByText('Its certificate is not checked')).not.toBeInTheDocument()

    await userEvent.click(within(card).getByRole('button', { name: 'Edit' }))
    const dialog = await screen.findByRole('dialog', { name: 'Edit Lab desk' })
    expect(within(dialog).getByRole('radio', { name: /Do not check/ })).toBeChecked()
    expect(within(dialog).getByText('Its certificate will not be checked')).toBeInTheDocument()
    await userEvent.click(within(dialog).getByRole('radio', { name: /Check the certificate/ }))
    expect(within(dialog).queryByText('Its certificate will not be checked')).not.toBeInTheDocument()
    await userEvent.click(within(dialog).getByRole('radio', { name: /Do not check/ }))
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'PATCH')?.body).toMatchObject({ tls: 'Off', tlsCa: null }))
  })

  it('an API is added by its OpenAPI document, its writes marked as asking first', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/tools': () => ({ json: [] }),
      'POST /api/admin/tools/servers/test': () => ({
        json: { ok: true, url: 'https://pets.example.test/v1', tools: [{ name: 'pets__list_pets', description: 'Lists the pets (GET /pets)', asksFirst: false }, { name: 'pets__add_pet', description: 'Adds a pet (POST /pets)', asksFirst: true }] },
      }),
      'POST /api/admin/tools/servers': () => ({ status: 201, json: { id: 's2', toolId: 'mcp:s2' } }),
    })
    renderApp('/admin/tools')
    await userEvent.click(await screen.findByRole('button', { name: 'Add a server or API' }))
    const dialog = await screen.findByRole('dialog', { name: 'Add a server or API' })
    await userEvent.click(within(dialog).getByRole('tab', { name: 'API (OpenAPI)' }))
    await userEvent.type(within(dialog).getByLabelText('Name'), 'Pets')
    await userEvent.type(within(dialog).getByLabelText("Or the document's address"), 'https://pets.example.test/openapi.json')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Read it' }))
    expect(await within(dialog).findByText('2 operations at https://pets.example.test/v1')).toBeInTheDocument()
    expect(within(dialog).getAllByText('asks first')).toHaveLength(1)
    await userEvent.click(within(dialog).getByRole('button', { name: 'Add API' }))
    await waitFor(() =>
      expect(calls.find((c) => c.method === 'POST' && c.path === '/api/admin/tools/servers')?.body).toMatchObject({ name: 'Pets', url: '', spec: null, specUrl: 'https://pets.example.test/openapi.json' }),
    )
  })
})
