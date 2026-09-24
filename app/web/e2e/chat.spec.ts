import { expect, test, type Page } from '@playwright/test'
import { watchConsole } from './helpers.ts'

// With a real model behind the gateway (the deployed stack): E2E_CHAT=1.
// Without one (CI), only the "no gateway" behaviour is checked.
const live = process.env.E2E_CHAT === '1'

/** On a phone the list of chats is folded behind "Chats". True when it was. */
async function openListOnPhone(page: Page): Promise<boolean> {
  const toggle = page.getByRole('button', { name: 'Chats', exact: true })
  if (!(await toggle.isVisible())) return false
  await toggle.click()
  return true
}

async function ask(page: Page, text: string) {
  await page.getByRole('textbox', { name: 'Message' }).fill(text)
  await page.getByRole('textbox', { name: 'Message' }).press('Enter')
}

test.describe('chat without a model', () => {
  test.skip(live, 'only where no gateway answers')

  test('sending says the model cannot be reached, and the chat is kept', async ({ page }) => {
    // Unique: every project and retry shares one database.
    const text = `hello there ${Date.now()}`
    await page.goto('/chat')
    await ask(page, text)
    await expect(page.getByRole('alert')).toContainText(/not reachable|could not answer/)
    await expect(page).toHaveURL(/\/chat\/[0-9a-f-]{36}$/)
    await openListOnPhone(page)
    await expect(page.getByRole('link', { name: text })).toBeVisible()
  })
})

test.describe('chat with the model', () => {
  test.skip(!live, 'needs the deployed stack (E2E_CHAT=1)')
  test.setTimeout(180_000)

  test('an answer streams, is kept, and survives a reload', async ({ page }) => {
    const errors = watchConsole(page)
    await page.goto('/chat')
    await page.getByLabel('Thinking').selectOption('off')
    await ask(page, 'Reply with exactly the word: pineapple')
    const answer = page.getByLabel('Answer').last()
    await expect(answer).toContainText(/pineapple/i, { timeout: 120_000 })
    await expect(page.getByRole('button', { name: 'Send' })).toBeVisible({ timeout: 120_000 })
    await expect(answer.getByText(/\d+ in .* out/)).toBeVisible()
    await page.reload()
    await expect(page.getByLabel('Answer').last()).toContainText(/pineapple/i)
    await expect(page.getByRole('textbox', { name: 'Chat title' })).toHaveValue('Reply with exactly the word: pineapple')
    expect(errors).toEqual([])
  })

  test('thinking is shown when it is on', async ({ page }) => {
    await page.goto('/chat')
    await page.getByLabel('Thinking').selectOption('low')
    await ask(page, 'Is 91 a prime number? Answer yes or no.')
    await expect(page.getByLabel('Answer').last().getByText(/Thought process|Thinking…/)).toBeVisible({ timeout: 120_000 })
    await expect(page.getByRole('button', { name: 'Send' })).toBeVisible({ timeout: 120_000 })
  })

  test('stop keeps what was written, and regenerate answers again', async ({ page }) => {
    await page.goto('/chat')
    await page.getByLabel('Thinking').selectOption('off')
    await ask(page, 'Count from 1 to 400, one number per line, no other text.')
    const answer = page.getByLabel('Answer').last()
    await expect(answer).toContainText('10', { timeout: 120_000 })
    await page.getByRole('button', { name: 'Stop' }).click()
    await expect(answer.getByText('Stopped.')).toBeVisible({ timeout: 30_000 })
    await expect(answer).not.toContainText('400')
    await answer.getByRole('button', { name: 'Regenerate' }).click()
    await expect(page.getByRole('button', { name: 'Stop' })).toBeVisible()
    await page.getByRole('button', { name: 'Stop' }).click()
    await expect(page.getByLabel('Answer').last().getByText('Stopped.')).toBeVisible({ timeout: 30_000 })
  })

  test('an attached file is read', async ({ page }) => {
    await page.goto('/chat')
    await page.getByLabel('Thinking').selectOption('off')
    await page.locator('input[type=file]').setInputFiles({ name: 'secret.txt', mimeType: 'text/plain', buffer: Buffer.from('The launch code word is TANGERINE-42.\n') })
    await expect(page.getByText('secret.txt')).toBeVisible()
    await ask(page, 'What is the launch code word in the attached file? Reply with it only.')
    await expect(page.getByLabel('Answer').last()).toContainText('TANGERINE-42', { timeout: 120_000 })
    await expect(page.getByLabel('You').last().getByText('secret.txt')).toBeVisible()
  })

  test('code blocks are highlighted and can be copied', async ({ page, context }) => {
    await context.grantPermissions(['clipboard-read', 'clipboard-write'])
    await page.goto('/chat')
    await page.getByLabel('Thinking').selectOption('off')
    await ask(page, 'Reply with only this, exactly, including the three backticks on their own lines:\n```python\nprint("hello world")\n```')
    const code = page.getByLabel('Answer').last().locator('.code')
    await expect(code).toBeVisible({ timeout: 120_000 })
    // Copy once the answer is complete: while it streams, each piece redraws the block.
    await expect(page.getByRole('button', { name: 'Send' })).toBeVisible({ timeout: 120_000 })
    await expect(code.locator('.hljs-built_in, .hljs-string').first()).toBeVisible()
    await code.getByRole('button', { name: 'Copy' }).click()
    await expect(code.getByRole('button', { name: 'Copied' })).toBeVisible()
    expect(await page.evaluate(() => navigator.clipboard.readText())).toContain('print')
  })

  test('chats are listed, searchable, renamed and deleted', async ({ page }) => {
    await page.goto('/chat')
    await page.getByLabel('Thinking').selectOption('off')
    await ask(page, 'Reply with the single word: ok')
    await expect(page.getByRole('button', { name: 'Send' })).toBeVisible({ timeout: 120_000 })
    const title = page.getByRole('textbox', { name: 'Chat title' })
    const name = `Renamed ${Date.now()}`
    await title.fill(name)
    await title.blur()
    const phone = await openListOnPhone(page)
    await page.getByRole('searchbox', { name: 'Search chats' }).fill(name)
    await expect(page.getByRole('link', { name })).toBeVisible()
    if (phone) await page.getByRole('button', { name: 'Hide chats' }).click()
    page.once('dialog', (d) => d.accept())
    await page.getByRole('button', { name: 'Delete' }).click()
    await expect(page).toHaveURL(/\/chat$/)
    await expect(page.getByRole('link', { name })).toHaveCount(0)
  })
})

// With the Argus test fixture up and a person who cannot read eal-core:
//   E2E_ARGUS_USER=dev_beta E2E_ARGUS_PASSWORD=...
test.describe('chat with Argus', () => {
  test.skip(!live || !process.env.E2E_ARGUS_PASSWORD, 'needs the Argus test fixture')
  test.use({ storageState: { cookies: [], origins: [] } })
  test.setTimeout(240_000)

  test('someone without access is told which repository and whom to ask', async ({ page }) => {
    await page.goto('/login')
    await page.getByLabel('Username or email').fill(process.env.E2E_ARGUS_USER ?? 'dev_beta')
    await page.getByLabel('Password').fill(process.env.E2E_ARGUS_PASSWORD!)
    await page.getByRole('button', { name: 'Sign in' }).click()
    await expect(page).not.toHaveURL(/\/login/)
    await page.goto('/chat')
    await expect(page.getByLabel('Search our code (Argus)')).toBeChecked()
    await page.getByLabel('Thinking').selectOption('off')
    await ask(page, "In our organisation's code, where is the function DecodeFrame defined? Look it up.")
    const note = page.getByRole('note')
    await expect(note).toContainText('You do not have access to some of this code.', { timeout: 180_000 })
    await expect(note).toContainText('root/eal-core')
    // Which of Argus's tools the model picks varies; that one ran is what matters.
    await expect(page.getByLabel('Answer').last().locator('.tool-name').first()).toBeVisible()
  })
})
