import { expect, test } from '@playwright/test'
import { expectAccessible, screenshot, watchConsole } from './helpers.ts'

const headers = { 'X-Requested-With': 'e2e' }
// With the picture server and the studio behind the stack (the deployed stack): E2E_CHAT=1.
const live = process.env.E2E_CHAT === '1'

interface Item { id: string }

const scene = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 400 300">
  <rect id="sky" width="400" height="300" fill="#bfe3ff"/>
  <circle id="sun" cx="320" cy="70" r="36" fill="#ffc83d"/>
  <g id="boat"><path d="M140 200 L260 200 L240 230 L160 230 Z" fill="#d33"/><path d="M200 120 L200 198 L250 198 Z" fill="#fff"/></g>
  <path id="waves" d="M0 230 Q50 215 100 230 T200 230 T300 230 T400 230 V300 H0 Z" fill="#2f86d6"/>
  <script>alert(1)</script>
</svg>`

test('an SVG of one’s own is made safe, its part picked on the stage and given a motion that plays', async ({ page }, info) => {
  test.skip(!live, 'needs the deployed stack (E2E_CHAT=1)')
  test.setTimeout(240_000)
  const errors = watchConsole(page)
  const before = new Set<string>()
  const list = async () => ((await (await page.request.get('/api/media/items', { headers })).json()) as { items: Item[] }).items
  for (const i of await list()) before.add(i.id)
  const name = `harbour-${Date.now() % 100000}`
  try {
    await page.goto('/media')
    await page.getByRole('radiogroup', { name: 'Make' }).getByRole('radio', { name: 'Vector' }).click()
    await page.getByLabel('An SVG file').setInputFiles({ name: `${name}.svg`, mimeType: 'image/svg+xml', buffer: Buffer.from(scene) })
    await expect(page.getByText(`${name}.svg is in the studio, made safe`, { exact: false })).toBeVisible()

    const gallery = page.getByRole('list', { name: 'Your pictures, drawings and clips' })
    await gallery.getByRole('button', { name: `Uploaded: ${name}` }).click()
    const dialog = page.getByRole('dialog')
    const stage = dialog.frameLocator('iframe[title="The drawing"]')
    await expect(stage.locator('#sun')).toBeVisible()
    await expect(dialog.getByText('It stands still: give it motions.')).toBeVisible()

    // A part picked by a click on the stage, given a motion, played.
    await dialog.getByRole('button', { name: /Pick a part/ }).click()
    // Picked where it is on the page (the stage itself takes no pointer: what is over it reads where it is).
    const hull = (await stage.locator('#boat path').first().boundingBox())!
    await page.mouse.move(hull.x + hull.width / 2, hull.y + hull.height / 2)
    await expect(stage.locator('#boat.arena-hover')).toBeAttached()
    await page.mouse.click(hull.x + hull.width / 2, hull.y + hull.height / 2)
    await expect(dialog.getByText('#boat', { exact: true })).toBeVisible()
    await dialog.getByRole('button', { name: /Give it a motion/ }).click()
    await dialog.getByRole('button', { name: /Play these/ }).click()
    await expect(dialog.getByRole('slider', { name: 'Time' })).toBeVisible()
    await expect(dialog.getByText(/^Plays 3\.0 s, then loops\.$/)).toBeVisible()
    await expect(stage.locator('svg style')).toContainText('@keyframes k1')
    await screenshot(page, info, 'vector-editor')
    await expectAccessible(page, info, 'vector-editor', ['iframe[title="The drawing"]'])

    await dialog.getByRole('tab', { name: 'Versions' }).click()
    await expect(dialog.getByRole('list', { name: 'Versions' }).getByRole('listitem')).toHaveCount(2)
    await page.keyboard.press('Escape')
    expect(errors).toEqual([])
  } finally {
    for (const i of await list()) if (!before.has(i.id)) await page.request.delete(`/api/media/items/${i.id}`, { headers })
  }
})

test('an illustration from words comes in named parts that move as asked, and is made a GIF', async ({ page, isMobile }, info) => {
  test.skip(!live || isMobile, 'needs the deployed stack (E2E_CHAT=1); one run is enough')
  test.setTimeout(600_000)
  const errors = watchConsole(page)
  const before = new Set<string>()
  const list = async () => ((await (await page.request.get('/api/media/items', { headers })).json()) as { items: Item[] }).items
  for (const i of await list()) before.add(i.id)
  const words = `a red fox sitting under a crescent moon, stars around (e2e ${Date.now() % 100000})`
  try {
    await page.goto('/media')
    await page.getByRole('radiogroup', { name: 'Make' }).getByRole('radio', { name: 'Vector' }).click()
    await page.getByLabel('Describe the drawing').fill(words)
    await page.getByRole('button', { name: /Make it/ }).click()
    const gallery = page.getByRole('list', { name: 'Your pictures, drawings and clips' })
    const drawing = gallery.getByRole('button', { name: `Traced into SVG: ${words}` })
    await expect(drawing).toBeVisible({ timeout: 300_000 })
    await drawing.click()
    const dialog = page.getByRole('dialog')
    const stage = dialog.frameLocator('iframe[title="The drawing"]')
    // Its parts named as the words say (the model may call them fox or red-fox, moon or crescent-moon).
    await expect(stage.locator('g[id*="fox"]').first()).toBeAttached()
    await expect(stage.locator('g[id*="moon"]').first()).toBeAttached()

    await dialog.getByLabel('Animate by words').fill('the stars twinkle one after another, the moon glows softly, the fox gently breathes')
    await dialog.getByRole('button', { name: /Animate it/ }).click()
    await expect(dialog.getByRole('slider', { name: 'Time' })).toBeVisible({ timeout: 180_000 })
    await expect(dialog.getByRole('list', { name: 'Its motions' }).getByRole('listitem')).not.toHaveCount(0)
    await screenshot(page, info, 'vector-animated')

    await dialog.getByRole('tab', { name: 'Export' }).click()
    await dialog.getByRole('radio', { name: 'GIF' }).click()
    await dialog.getByRole('button', { name: /Make the GIF/ }).click()
    await expect(dialog.getByRole('link', { name: /Download .*\.gif/ })).toBeVisible({ timeout: 180_000 })
    await screenshot(page, info, 'vector-exported')
    await page.keyboard.press('Escape')
    expect(errors).toEqual([])
  } finally {
    for (const i of await list()) if (!before.has(i.id)) await page.request.delete(`/api/media/items/${i.id}`, { headers })
  }
})
