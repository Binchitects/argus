import { screen, within } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { admin, fakeApi, member, renderApp } from '@/test/utils'

// What a person may not use (Admin → Access) is left out of the pages, not shown to fail with 403.
describe('controls one may not use are hidden', () => {
  it('Home shows the system summary only to one who may see monitoring', async () => {
    const calls = fakeApi({ ...member, permissions: ['people.read'] })
    renderApp('/')
    expect(await screen.findByText('Your credit this month')).toBeInTheDocument()
    expect(screen.queryByText('System')).toBeNull()
    expect(calls.some((c) => c.path === '/api/admin/overview')).toBe(false)
  })

  it('Home shows the system summary to a monitoring admin, not only the Owner', async () => {
    fakeApi({ ...member, permissions: ['monitoring.read'] })
    renderApp('/')
    expect(await screen.findByText('System')).toBeInTheDocument()
  })

  it('Home shows the system summary to the Owner, who holds every permission', async () => {
    fakeApi({ ...admin, permissions: ['monitoring.read', 'people.read'] })
    renderApp('/')
    expect(await screen.findByText('System')).toBeInTheDocument()
  })

  it('the account page leaves out API keys, voice, memory and the download of one’s data for one who may not use them', async () => {
    fakeApi({ ...member, denied: ['feature.api_keys', 'feature.speech', 'feature.memory', 'feature.export'] }, {
      'GET /api/account/keys': () => ({ json: { apiOff: true, keys: [], spend: 0, apiSpend: 0, apiCredit: null, standing: null } }),
    })
    renderApp('/account')
    expect(await screen.findByText('Your API access is off')).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'API keys' })).toBeNull()
    expect(screen.queryByRole('heading', { name: 'Voice' })).toBeNull()
    expect(screen.queryByRole('heading', { name: 'Memory' })).toBeNull()
    expect(screen.queryByRole('link', { name: /Download your data/ })).toBeNull()
  })

  it('meetings are listed but none is recorded or uploaded without feature.speech', async () => {
    fakeApi({ ...member, denied: ['feature.speech'] }, { 'GET /api/meetings': () => ({ json: [] }) })
    renderApp('/meetings')
    expect(await screen.findByText('No meetings yet')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Upload a recording/ })).toBeNull()
    expect(screen.queryByRole('button', { name: /Record a meeting/ })).toBeNull()
  })

  it('a room’s panel has no export without feature.export', async () => {
    const room = {
      id: 'r1', title: 'Launch', thinking: null, tools: [], useCodeIntel: false, model: null, systemPrompt: null, temperature: null, topP: null, maxTokens: null,
      currentLeafId: null, archivedAt: null, forkedFrom: null, createdAt: '', updatedAt: '', messages: [],
      room: { role: 'member', membersInvite: false, answeringFor: null, groups: [], members: [{ id: 'o1', name: 'Olivia', role: 'owner' }, { id: member.id, name: member.displayName, role: 'member' }] },
    }
    fakeApi({ ...member, denied: ['feature.export'] }, {
      'GET /api/chat/config': () => ({ json: { model: 'M', models: [], presets: [], defaultThinking: null, codeIntel: false, tools: [], gitlabUrl: null, maxUploadBytes: 1, imageTypes: [], denied: ['feature.export'] } }),
      'GET /api/chat/conversations': () => ({ json: [{ id: 'r1', title: 'Launch', updatedAt: new Date().toISOString(), isRoom: true, mine: false }] }),
      'GET /api/chat/conversations/r1': () => ({ json: room }),
      'GET /api/rooms/languages': () => ({ json: [] }),
      'GET /api/rooms/r1/events': () => ({ events: [], hang: true }),
    })
    renderApp('/chat/r1')
    const people = await screen.findByRole('button', { name: /people in this room|2 people/i })
    people.click()
    const panel = await screen.findByRole('dialog')
    expect(within(panel).getByText('Olivia')).toBeInTheDocument()
    expect(within(panel).queryByRole('region', { name: 'Export' })).toBeNull()
  })
})
