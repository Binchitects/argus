import { expect, test } from '@playwright/test'
import { expectAccessible, screenshot, watchConsole } from './helpers.ts'

const live = process.env.E2E_CHAT === '1'
const headers = { 'X-Requested-With': 'e2e' }

test('a skill is made with parameters, chosen with /, filled in above the box and sent as a skill', async ({ page, isMobile }, info) => {
  test.setTimeout(240_000)
  const errors = watchConsole(page)
  const name = `echo-${isMobile ? 'phone' : 'desk'}-${Date.now() % 100000}`
  await page.goto('/skills')
  await expect(page.getByRole('heading', { level: 1, name: 'Skills' })).toBeVisible()
  await page.getByRole('button', { name: 'New skill' }).first().click()
  const dialog = page.getByRole('dialog', { name: 'New skill' })
  await dialog.getByLabel('Slash name').fill(name)
  await dialog.getByLabel('Title').fill('Echo a word')
  await dialog.getByLabel('When to use it').fill('Only when the person asks to echo a word.')
  await dialog.getByLabel('Instructions').fill('Reply with only this, exactly, in lowercase: skill {{word}} $ARGUMENTS')
  await dialog.getByLabel('Title').click()
  const word = dialog.getByRole('listitem', { name: 'word' })
  await word.getByLabel('What it is').fill('One word to echo')
  await expectAccessible(page, info, 'skill-form')
  await dialog.getByRole('button', { name: 'Add skill' }).click()

  let chat: string | null = null
  try {
    const card = page.getByRole('region', { name: `/${name}` })
    await expect(card).toContainText('Echo a word')
    await expect(card).toContainText('Used on its own')
    await expectAccessible(page, info, 'skills')
    await screenshot(page, info, `skills${isMobile ? '-phone' : ''}`)

    await page.goto('/chat')
    const box = page.getByRole('textbox', { name: 'Message' })
    await box.fill(`/${name}`)
    await page.getByRole('listbox', { name: 'Skills' }).getByRole('option', { name: new RegExp(name) }).click()
    const chosen = page.getByRole('group', { name: `Use /${name}` })
    await expect(chosen).toBeVisible()
    await expect(box).toHaveValue('')
    // Not sent while its parameter is empty.
    await page.getByRole('button', { name: 'Send', exact: true }).click()
    await expect(chosen.getByText('Fill this in to send.')).toBeVisible()
    await chosen.getByRole('textbox', { name: /Word/ }).fill('banana')
    await expectAccessible(page, info, 'skill-chosen')
    await screenshot(page, info, `skill-chosen${isMobile ? '-phone' : ''}`)
    await box.fill('please')
    await box.press('Enter')
    const used = page.getByRole('figure', { name: `Used the skill /${name}` })
    await expect(used).toContainText('banana')
    await expect(chosen).toHaveCount(0)
    await expect.poll(() => page.url()).toMatch(/\/chat\/[0-9a-f-]{36}$/)
    chat = page.url().split('/').pop()!
    if (live) {
      await expect(page.getByRole('region', { name: 'Answer' }).last()).toContainText(/skill banana/i, { timeout: 180_000 })
      await used.getByRole('button', { name: 'Instructions' }).click()
      await expect(used).toContainText('skill banana please')
    }
    expect(errors).toEqual([])
  } finally {
    // Whatever happened, the test's skill and its chat go.
    if (chat) await page.request.delete(`/api/chat/conversations/${chat}`, { headers })
    for (const s of (await (await page.request.get('/api/skills')).json()).skills.filter((s: { name: string }) => s.name === name))
      await page.request.delete(`/api/skills/${s.id}`, { headers })
  }
})
