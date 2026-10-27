import { expect, test } from '@playwright/test'
import { expectAccessible, screenshot, watchConsole } from './helpers.ts'

const live = process.env.E2E_CHAT === '1'
const H = { 'X-Requested-With': 'e2e' }

test('the science lab works a problem as a notebook, its check proved by SymPy, and lists it with its checks', async ({ page, isMobile }, info) => {
  test.setTimeout(300_000)
  const errors = watchConsole(page)
  await page.goto('/lab')
  await expect(page.getByRole('heading', { level: 1, name: 'Science lab' })).toBeVisible()
  const fields = await (await page.request.get('/api/lab/fields')).json()
  if (!fields.available) {
    // No sandbox (or no Python for this person): the page says why, and offers no problem to work.
    await expect(page.getByText(fields.why)).toBeVisible()
    await expect(page.getByRole('button', { name: 'Work it' })).toHaveCount(0)
    await expectAccessible(page, info, 'lab-unavailable')
    test.skip(true, `the lab is not available here: ${fields.why}`)
  }
  await expect(page.getByRole('radio', { name: /Mathematics/ })).toBeChecked()
  await expectAccessible(page, info, 'lab')
  await screenshot(page, info, `lab${isMobile ? '-phone' : ''}`)
  test.skip(!live, 'working a problem needs the deployed stack (E2E_CHAT=1)')

  await page.getByRole('textbox', { name: 'The problem' }).fill('Check with math_check that sin(x)**2 + cos(x)**2 equals 1, then say the result in one line.')
  await page.getByRole('button', { name: 'Work it' }).click()
  await expect(page).toHaveURL(/\/lab\/[0-9a-f-]{36}$/)
  const id = page.url().split('/').pop()!
  try {
    await expect(page.getByRole('region', { name: 'The problem' })).toContainText('sin(x)**2 + cos(x)**2')
    const check = page.getByRole('region', { name: 'Check' }).first()
    await expect(check).toContainText('Proved', { timeout: 240_000 })
    await expect(check).toContainText('simplifies to 0')
    await expect(page.getByRole('main').getByText(/\d+ proved/).first()).toBeVisible()
    await expect(page.getByRole('list', { name: 'Notebook' })).toHaveAttribute('aria-busy', 'false', { timeout: 120_000 })
    await expectAccessible(page, info, 'lab-notebook')
    await screenshot(page, info, `lab-notebook${isMobile ? '-phone' : ''}`)

    // In the lab's list with its check counted; not among the chats.
    await page.goto('/lab')
    const row = page.getByRole('list', { name: 'Notebooks' }).locator(`a[href="/lab/${id}"]`)
    await expect(row).toContainText(/\d+ proved/)
    const chats = (await (await page.request.get('/api/chat/conversations')).json()) as { id: string }[]
    expect(chats.some((c) => c.id === id)).toBe(false)
  } finally {
    expect((await page.request.delete(`/api/chat/conversations/${id}`, { headers: H })).status()).toBe(204)
  }
  expect(errors).toEqual([])
})

test('the lab balances a reaction checked atom by atom, and draws a molecule', async ({ page, isMobile }, info) => {
  test.setTimeout(300_000)
  test.skip(!live, 'working a problem needs the deployed stack (E2E_CHAT=1)')
  const fields = await (await page.request.get('/api/lab/fields')).json()
  test.skip(!fields.available, `the lab is not available here: ${fields.why}`)
  const errors = watchConsole(page)
  await page.goto('/lab')
  // A choice card: its radio is hidden, its label takes the click.
  await page.locator('label', { has: page.getByRole('radio', { name: /Chemistry/ }) }).click()
  await expect(page.getByRole('radio', { name: /Chemistry/ })).toBeChecked()
  await page.getByRole('textbox', { name: 'The problem' }).fill('Balance Fe + O2 -> Fe2O3 with chem_balance, then show ethanol (SMILES CCO) with chem_molecule.')
  await page.getByRole('button', { name: 'Work it' }).click()
  await expect(page).toHaveURL(/\/lab\/[0-9a-f-]{36}$/)
  const id = page.url().split('/').pop()!
  try {
    const reaction = page.getByRole('region', { name: 'Reaction' }).first()
    await expect(reaction).toContainText('Balanced, checked', { timeout: 240_000 })
    await expect(reaction).toContainText('4 Fe + 3 O2 → 2 Fe2O3')
    const molecule = page.getByRole('region', { name: 'Molecule' }).first()
    await expect(molecule).toContainText('C2H6O', { timeout: 120_000 })
    await expect(molecule.getByRole('img', { name: 'C2H6O drawn' })).toBeVisible()
    await expect(page.getByRole('list', { name: 'Notebook' })).toHaveAttribute('aria-busy', 'false', { timeout: 120_000 })
    await expectAccessible(page, info, 'lab-chemistry')
    await screenshot(page, info, `lab-chemistry${isMobile ? '-phone' : ''}`)
  } finally {
    expect((await page.request.delete(`/api/chat/conversations/${id}`, { headers: H })).status()).toBe(204)
  }
  expect(errors).toEqual([])
})

test('the lab proves a theorem with Lean and Mathlib, checked by Lean', async ({ page, isMobile }, info) => {
  test.setTimeout(300_000)
  test.skip(!live, 'working a problem needs the deployed stack (E2E_CHAT=1)')
  const fields = await (await page.request.get('/api/lab/fields')).json()
  const maths = fields.fields.find((f: { id: string }) => f.id === 'mathematics')
  test.skip(!fields.available || !maths?.tools.includes('proofs'), 'the proof checker (the lean profile) does not run here')
  const errors = watchConsole(page)
  await page.goto('/lab')
  await page.getByRole('textbox', { name: 'The problem' }).fill(
    'Prove with lean_check, in Lean 4 with Mathlib, that for all real numbers a and b, (a + b)^2 = a^2 + 2*a*b + b^2. The proof is `by ring`.',
  )
  await page.getByRole('button', { name: 'Work it' }).click()
  await expect(page).toHaveURL(/\/lab\/[0-9a-f-]{36}$/)
  const id = page.url().split('/').pop()!
  try {
    await expect(page.getByRole('region', { name: 'Proof' }).filter({ hasText: 'Checked by Lean' }).first()).toBeVisible({ timeout: 240_000 })
    await expect(page.getByRole('list', { name: 'Notebook' })).toHaveAttribute('aria-busy', 'false', { timeout: 120_000 })
    await expectAccessible(page, info, 'lab-proof')
    await screenshot(page, info, `lab-proof${isMobile ? '-phone' : ''}`)
  } finally {
    expect((await page.request.delete(`/api/chat/conversations/${id}`, { headers: H })).status()).toBe(204)
  }
  expect(errors).toEqual([])
})
