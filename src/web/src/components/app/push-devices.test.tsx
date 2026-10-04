import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { fakeApi, member, renderApp } from '@/test/utils'

// The browser's side (service worker, Push API) as a browser that supports it would answer.
const browser = vi.hoisted(() => ({
  support: 'supported' as 'supported' | 'unsupported' | 'blocked',
  here: null as string | null,
  subscribed: [] as string[],
  unsubscribed: 0,
}))
vi.mock('@/lib/pwa', async (original) => ({
  ...(await original<typeof import('@/lib/pwa')>()),
  pushSupport: () => browser.support,
  currentSubscription: async () => (browser.here ? { endpoint: browser.here } : null),
  subscribe: async (key: string) => {
    browser.subscribed.push(key)
    browser.here = 'https://fcm.googleapis.com/fcm/send/this-one'
    return { endpoint: browser.here, keys: { p256dh: 'BPk', auth: 'au' } }
  },
  unsubscribe: async () => {
    browser.unsubscribed++
    const was = browser.here
    browser.here = null
    return was
  },
}))

const phone = { id: 'd1', device: 'Chrome on Android', endpoint: 'https://fcm.googleapis.com/fcm/send/phone', createdAt: '2026-10-01T10:00:00Z', lastSentAt: null, lastError: null }

describe('push notifications in your account', () => {
  it('turns push on for this device, lists the devices, tests and removes them', async () => {
    browser.support = 'supported'
    browser.here = null
    let devices = [phone]
    const calls = fakeApi(member, {
      'GET /api/push': () => ({ json: { available: true, publicKey: 'BKEY', devices } }),
      'POST /api/push/subscriptions': (body) => {
        const b = body as { endpoint: string }
        devices = [...devices, { ...phone, id: 'd2', device: 'Firefox on Linux', endpoint: b.endpoint }]
        return { json: devices.at(-1) }
      },
      'POST /api/push/test': () => ({ json: { sent: 2, failed: 0 } }),
      'DELETE /api/push/subscriptions/d2': () => {
        devices = devices.filter((d) => d.id !== 'd2')
        return { status: 204 }
      },
    })
    renderApp('/account')
    const list = await screen.findByRole('list', { name: 'Devices with push notifications' })
    expect(within(list).getByRole('listitem', { name: 'Chrome on Android' })).toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Turn on for this device' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'POST' && c.path === '/api/push/subscriptions')?.body).toEqual({
      endpoint: 'https://fcm.googleapis.com/fcm/send/this-one', keys: { p256dh: 'BPk', auth: 'au' },
    }))
    expect(browser.subscribed).toEqual(['BKEY'])
    const mine = await within(list).findByRole('listitem', { name: 'Firefox on Linux' })
    expect(within(mine).getByText('This device')).toBeInTheDocument()
    expect(await screen.findByText('On for this device')).toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Send a test' }))
    await waitFor(() => expect(calls.some((c) => c.path === '/api/push/test')).toBe(true))

    await userEvent.click(within(mine).getByRole('button', { name: 'Remove Firefox on Linux' }))
    await waitFor(() => expect(calls.some((c) => c.method === 'DELETE' && c.path === '/api/push/subscriptions/d2')).toBe(true))
    expect(browser.unsubscribed).toBe(1)
    expect(await screen.findByRole('button', { name: 'Turn on for this device' })).toBeInTheDocument()
  })

  it('says why push cannot be turned on: no data key, or a browser without it', async () => {
    fakeApi(member, { 'GET /api/push': () => ({ json: { available: false, publicKey: null, devices: [] } }) })
    const first = renderApp('/account')
    expect(await screen.findByText(/APP_KEY is not set/)).toBeInTheDocument()
    first.unmount()

    browser.support = 'unsupported'
    fakeApi(member, { 'GET /api/push': () => ({ json: { available: true, publicKey: 'BKEY', devices: [] } }) })
    renderApp('/account')
    expect(await screen.findByText(/This browser cannot get push notifications/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Turn on for this device' })).not.toBeInTheDocument()
  })
})
