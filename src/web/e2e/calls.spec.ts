import { fileURLToPath } from 'node:url'
import { expect, test, type Page } from '@playwright/test'
import { expectAccessible, screenshot, watchConsole } from './helpers.ts'

const headers = { 'X-Requested-With': 'e2e' }
// With speech-to-text behind the stack (the deployed stack): E2E_CHAT=1.
const live = process.env.E2E_CHAT === '1'
// Chrome's microphone plays this, again and again: Kokoro's am_michael saying the launch is on Friday.
const voice = fileURLToPath(new URL('./fixtures/call-voice.wav', import.meta.url))

test.use({
  permissions: ['microphone'],
  launchOptions: { args: ['--use-fake-ui-for-media-stream', '--use-fake-device-for-media-stream', `--use-file-for-fake-audio-capture=${voice}`] },
})

/** The page's connections to the others in the call, watched from its start. */
async function watchPeers(page: Page) {
  await page.addInitScript(() => {
    const Original = window.RTCPeerConnection
    const peers: RTCPeerConnection[] = []
    ;(window as unknown as { peers: RTCPeerConnection[] }).peers = peers
    window.RTCPeerConnection = class extends Original {
      constructor(config?: RTCConfiguration) {
        super(config)
        peers.push(this)
      }
    }
  })
}

const connected = (page: Page) => page.evaluate(() => (window as unknown as { peers: RTCPeerConnection[] }).peers.some((p) => p.connectionState === 'connected'))

test('two pages in a room\'s call reach each other, and what is said shows as captions', async ({ context, isMobile }, info) => {
  test.skip(!live, 'needs the deployed stack (E2E_CHAT=1)')
  test.setTimeout(240_000)
  const made = await context.request.post('/api/rooms', { headers, data: { title: `Call check (${isMobile ? 'phone' : 'desk'})`, userIds: [] } })
  expect(made.status()).toBe(201)
  const room = ((await made.json()) as { id: string }).id
  try {
    const first = await context.newPage()
    const second = await context.newPage()
    const errors = [...watchConsole(first), ...watchConsole(second)]
    await watchPeers(first)
    await watchPeers(second)
    await first.goto(`/chat/${room}`)
    await second.goto(`/chat/${room}`)

    await first.getByRole('button', { name: 'Start a call' }).click()
    const firstBar = first.getByRole('region', { name: 'The call' })
    await expect(firstBar.getByRole('status', { name: 'In the call' })).toContainText('waiting for the others')
    const secondBar = second.getByRole('region', { name: 'The call' })
    await expect(secondBar.getByRole('status', { name: 'In the call' })).toContainText('in a call')
    await secondBar.getByRole('button', { name: 'Join' }).click()

    // The two pages reach each other directly (the server passed only their signals).
    await expect.poll(() => connected(second), { timeout: 30_000 }).toBe(true)
    await expect.poll(() => connected(first), { timeout: 30_000 }).toBe(true)
    // What the microphone hears is written down for the call.
    await expect(secondBar.getByRole('list', { name: 'Captions' })).toContainText(/Friday/i, { timeout: 120_000 })
    await screenshot(second, info, `call${isMobile ? '-phone' : ''}`)
    await expectAccessible(second, info, 'call')

    await secondBar.getByRole('button', { name: 'Mute' }).click()
    await expect(firstBar.getByRole('list', { name: 'Muted' }).getByRole('listitem')).toHaveCount(1)
    await secondBar.getByRole('button', { name: 'Leave' }).click()
    await expect(firstBar.getByRole('status', { name: 'In the call' })).toContainText('waiting for the others')
    await firstBar.getByRole('button', { name: 'Leave' }).click()
    await expect(first.getByRole('region', { name: 'The call' })).toHaveCount(0)
    expect(errors).toEqual([])
  } finally {
    await context.request.delete(`/api/chat/conversations/${room}`, { headers })
  }
})
