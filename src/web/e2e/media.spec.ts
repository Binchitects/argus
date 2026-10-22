import { expect, test } from '@playwright/test'
import { expectAccessible, screenshot, watchConsole } from './helpers.ts'

const headers = { 'X-Requested-With': 'e2e' }
// With the picture server behind the stack (the deployed stack): E2E_CHAT=1.
const live = process.env.E2E_CHAT === '1'

interface Item { id: string; prompt: string | null }

test('a picture is made from words, then changed only where painted', async ({ page, isMobile }, info) => {
  test.skip(!live, 'needs the deployed stack (E2E_CHAT=1)')
  test.setTimeout(600_000)
  const errors = watchConsole(page)
  const before = new Set<string>()
  const list = async () => ((await (await page.request.get('/api/media/items', { headers })).json()) as { items: Item[] }).items
  for (const i of await list()) before.add(i.id)
  const words = `A small red kite in a clear blue sky over green hills (${isMobile ? 'phone' : 'desk'})`
  try {
    await page.goto('/media')
    await page.getByLabel('Describe a picture').fill(words)
    await page.getByRole('radio', { name: 'Wide' }).click()
    await page.getByRole('button', { name: /Make it/ }).click()
    const gallery = page.getByRole('list', { name: 'Your pictures, drawings and clips' })
    // Its place in the gallery says what it does, then it is there.
    await expect(gallery.getByRole('listitem', { name: new RegExp(`^Drawing: ${words.slice(0, 20)}`) })).toBeVisible()
    const made = gallery.getByRole('button', { name: `Made from words: ${words}` })
    await expect(made).toBeVisible({ timeout: 300_000 })
    await screenshot(page, info, 'media-gallery')
    await expectAccessible(page, info, 'media-gallery')

    await made.click()
    const dialog = page.getByRole('dialog')
    await expect(dialog.getByRole('img', { name: words })).toBeVisible()
    await dialog.getByLabel('What to change').fill('a yellow hot air balloon')
    await dialog.getByRole('button', { name: /Paint the part/ }).click()
    const canvas = dialog.getByLabel('Paint over the part to change')
    const box = (await canvas.boundingBox())!
    await page.mouse.move(box.x + box.width * 0.15, box.y + box.height * 0.2)
    await page.mouse.down()
    await page.mouse.move(box.x + box.width * 0.3, box.y + box.height * 0.35, { steps: 8 })
    await page.mouse.move(box.x + box.width * 0.2, box.y + box.height * 0.4, { steps: 8 })
    await page.mouse.up()
    await screenshot(page, info, 'media-painting')
    await dialog.getByRole('button', { name: /Change the painted part/ }).click()
    await expect(dialog).toHaveCount(0)
    await expect(gallery.getByRole('button', { name: 'Changed: a yellow hot air balloon' })).toBeVisible({ timeout: 300_000 })

    await gallery.getByRole('button', { name: 'Changed: a yellow hot air balloon' }).click()
    await expect(page.getByRole('dialog').getByRole('button', { name: `Made from: ${words}` })).toBeVisible()
    await screenshot(page, info, 'media-changed')
    await page.keyboard.press('Escape')
    expect(errors).toEqual([])
  } finally {
    for (const i of await list()) if (!before.has(i.id)) await page.request.delete(`/api/media/items/${i.id}`, { headers })
  }
})

test('a storyboard is written from a few words and opens to be changed', async ({ page, isMobile }, info) => {
  test.skip(!live, 'needs the deployed stack (E2E_CHAT=1)')
  test.setTimeout(300_000)
  const errors = watchConsole(page)
  let story: string | null = null
  try {
    await page.goto('/media')
    await page.getByRole('radio', { name: 'Video' }).click()
    await page.getByLabel('What the video is about').fill(`A short welcome to our book club (${isMobile ? 'phone' : 'desk'}): we meet on the first Monday, read one novel a month.`)
    await page.getByRole('radio', { name: '3 shots' }).click()
    const written = page.waitForResponse((r) => r.url().endsWith('/api/media/stories') && r.request().method() === 'POST', { timeout: 180_000 })
    await page.getByRole('button', { name: /Write the storyboard/ }).click()
    story = ((await (await written).json()) as { id: string }).id
    const dialog = page.getByRole('dialog')
    const shots = dialog.getByRole('list', { name: 'Shots' })
    await expect(shots.getByLabel('What the narrator says').first()).not.toHaveValue('')
    expect(await shots.getByLabel('What is seen (in English)').count()).toBeGreaterThanOrEqual(2)
    await expect(dialog.getByRole('button', { name: /^Make the video$/ })).toBeEnabled()
    await screenshot(page, info, 'media-storyboard')
    await expectAccessible(page, info, 'media-storyboard')
    await page.keyboard.press('Escape')
    await expect(page.getByRole('region', { name: 'Your videos' })).toBeVisible()
    expect(errors).toEqual([])
  } finally {
    if (story) await page.request.delete(`/api/media/stories/${story}`, { headers })
  }
})
