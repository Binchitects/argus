import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { admin, fakeApi, renderApp } from '@/test/utils'

const tool = {
  id: 'time', title: 'Date and time', description: 'The time.', icon: 'clock', unavailable: null, custom: false,
  setting: { enabled: true, audience: 'Everyone', onByDefault: true, askFirst: false, groups: [], shownAs: null, customized: false, callsPerDay: null }, server: null,
}

const customization = {
  id: 'time', title: 'Date and time', description: 'The time.', problem: null,
  functions: [
    { name: 'current_time', description: 'The current time.', parameters: [{ name: 'time_zone', description: 'IANA zone' }] },
    { name: 'days_between', description: 'Days between dates.', parameters: [] },
  ],
  setting: { title: null, description: null, instructions: null, replaceInstructions: false, disabledFunctions: [], askFirstFunctions: [], callsPerDay: null, functions: {} },
  groups: [],
}

describe('customizing a tool', () => {
  it('turns a function off, fixes an argument, limits calls a day, and shows the week’s use', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/tools': () => ({ json: [tool] }),
      'GET /api/admin/tools/usage': () => ({ json: [{ toolId: 'time', calls: 12, failed: 1, averageMs: 400, lastError: null, functions: [] }] }),
      'GET /api/admin/tools/custom': () => ({ json: [] }),
      'GET /api/admin/tools/time/customize': () => ({ json: customization }),
      'PUT /api/admin/tools/time/customize': () => ({ status: 204 }),
      'GET /api/admin/groups': () => ({ json: [] }),
    })
    renderApp('/admin/tools')
    const card = (await screen.findByRole('heading', { name: /Date and time/ })).closest('section')!
    expect(await within(card).findByText(/12 calls this week · 1 failed · 0.4 s on average/)).toBeInTheDocument()
    await userEvent.click(within(card).getByRole('button', { name: 'Customize' }))
    const dialog = await screen.findByRole('dialog')
    // days_between, the second function, is no longer offered to the model.
    const offered = await within(dialog).findAllByRole('switch', { name: 'Offered' })
    await userEvent.click(offered[1]!)
    await userEvent.type(within(dialog).getByLabelText('current_time: fixed arguments'), '{{"time_zone": "UTC"}')
    await userEvent.type(within(dialog).getByLabelText('Calls a day, per person'), '20')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save' }))
    await waitFor(() =>
      expect(calls.find((c) => c.method === 'PUT')?.body).toMatchObject({
        disabledFunctions: ['days_between'], callsPerDay: 20, functions: { current_time: { fixed: { time_zone: 'UTC' } } },
      }),
    )
  })
})
