import { fileURLToPath } from 'node:url'
import { expect, test } from '@playwright/test'
import { expectAccessible, screenshot, watchConsole } from './helpers.ts'

const headers = { 'X-Requested-With': 'e2e' }
// With speech-to-text, the studio's voice model and a chat model behind the stack (the deployed stack): E2E_CHAT=1.
const live = process.env.E2E_CHAT === '1'
// Two voices (Kokoro's af_heart and am_michael), five turns: Ana and Ben agree to launch on Friday and share three tasks.
const recording = fileURLToPath(new URL('./fixtures/meeting.mp3', import.meta.url))

test('an uploaded meeting is made into who said what and its minutes, and its action items are given', async ({ page, isMobile }, info) => {
  test.skip(!live, 'needs the deployed stack (E2E_CHAT=1)')
  test.setTimeout(600_000)
  const errors = watchConsole(page)
  const title = `Checkout launch (${isMobile ? 'phone' : 'desk'} ${Date.now() % 100000})`
  let id: string | null = null
  try {
    await page.goto('/meetings')
    await expect(page.getByRole('heading', { level: 1, name: 'Meetings' })).toBeVisible()
    await expectAccessible(page, info, 'meetings')
    await page.getByRole('textbox', { name: 'Title' }).fill(title)
    await page.getByLabel('Recording to upload').setInputFiles(recording)
    await page.waitForURL(/\/meetings\/[0-9a-f-]{36}$/)
    id = page.url().split('/').pop()!

    // Made in steps, then the minutes.
    const items = page.getByRole('list', { name: 'The action items' })
    await expect(items).toBeVisible({ timeout: 480_000 })
    const said = page.getByRole('list', { name: 'What was said' })
    await expect(said.getByRole('listitem')).not.toHaveCount(0)
    await expect(said).toContainText(/Friday/i)
    // Two voices told apart, named as they named each other.
    const speakers = page.getByLabel('Speakers', { exact: true })
    await expect(speakers.getByRole('textbox', { name: /Name of speaker/ })).toHaveCount(2)
    await expect(said.getByRole('button', { name: /: Ana$/ }).first()).toBeVisible()
    await expect(said.getByRole('button', { name: /: Ben$/ }).first()).toBeVisible()
    await expect(page.getByLabel('Decisions', { exact: true })).toContainText(/Friday/i)
    expect(await items.getByRole('listitem').count()).toBeGreaterThanOrEqual(2)
    await screenshot(page, info, `meeting${isMobile ? '-phone' : ''}`)
    await expectAccessible(page, info, 'meeting')

    // Ben is me here: his action items are mine.
    const ben = speakers.getByRole('listitem', { name: 'Ben' })
    await ben.getByRole('button', { name: 'It is me' }).click()
    await expect(ben).toContainText('Their action items go to')
    await expect(items.getByText(/^→ /).first()).toBeVisible()

    // A line plays the recording from there.
    await said.getByRole('button', { name: /: Ben$/ }).first().click()
    await expect.poll(() => page.getByLabel('The recording').evaluate((a: HTMLAudioElement) => a.currentTime)).toBeGreaterThan(0)
    expect(errors).toEqual([])
  } finally {
    if (id) await page.request.delete(`/api/meetings/${id}`, { headers })
  }
})
