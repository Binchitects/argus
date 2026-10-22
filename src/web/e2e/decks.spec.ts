import { expect, test } from '@playwright/test'
import { expectAccessible, screenshot, watchConsole } from './helpers.ts'

const headers = { 'X-Requested-With': 'e2e' }
// With a real model behind the gateway (the deployed stack): E2E_CHAT=1.
const live = process.env.E2E_CHAT === '1'

test('a deck is made from a few words, changed by asking, presented and downloaded', async ({ page, isMobile }, info) => {
  test.skip(!live, 'needs the deployed stack (E2E_CHAT=1)')
  test.setTimeout(300_000)
  const errors = watchConsole(page)
  let deck: string | null = null
  try {
    await page.goto('/decks')
    await page.getByLabel('Make a deck about…').fill(`A 4-slide deck (${isMobile ? 'phone' : 'desk'}) about our lunch club: when we meet, where, and how to join.`)
    await page.getByRole('button', { name: /Make it/ }).click()
    await expect(page).toHaveURL(/\/decks\/[0-9a-f-]+$/, { timeout: 180_000 })
    deck = page.url().split('/').pop() ?? null
    const slides = page.getByRole('list', { name: 'Slides' })
    await expect(slides.getByRole('button', { name: /^Slide 1:/ })).toBeVisible()
    const before = await slides.getByRole('button').count()
    await screenshot(page, info, 'deck')
    await expectAccessible(page, info, 'deck')

    await slides.getByRole('button', { name: /^Slide 1:/ }).click()
    await page.getByLabel('Change it by asking').fill('Add a slide after this one about the cost: lunch is 12 euros.')
    await page.getByRole('button', { name: /^Change$/ }).click()
    await expect(slides.getByRole('button')).toHaveCount(before + 1, { timeout: 180_000 })

    const download = page.waitForEvent('download')
    await page.getByRole('link', { name: /PowerPoint/ }).click()
    expect((await download).suggestedFilename()).toMatch(/\.pptx$/)

    await page.getByRole('button', { name: /Present/ }).click()
    await expect(page.getByRole('dialog', { name: /^Presenting, slide 1 of/ })).toBeVisible()
    await page.keyboard.press('ArrowRight')
    await expect(page.getByRole('dialog', { name: /^Presenting, slide 2 of/ })).toBeVisible()
    await screenshot(page, info, 'deck-presenting')
    await page.keyboard.press('Escape')
    await expect(page.getByRole('dialog', { name: /Presenting/ })).toHaveCount(0)
    expect(errors).toEqual([])
  } finally {
    if (deck) await page.request.delete(`/api/decks/${deck}`, { headers })
  }
})
