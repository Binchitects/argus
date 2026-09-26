import { expect, test } from '@playwright/test'
import { noGateway } from './helpers.ts'

test('the account shows the key and credit, and two-factor setup can be started and cancelled', async ({ page }) => {
  await page.goto('/account')
  await expect(page.getByRole('heading', { name: 'API key' })).toBeVisible()
  // Without a gateway the card says why there is no credit to show.
  await expect(noGateway ? page.getByRole('alert').filter({ hasText: /gateway|reach/i }) : page.getByText('Credit used')).toBeVisible()
  const setup = page.getByRole('button', { name: 'Set up' })
  if (await setup.isVisible()) {
    await setup.click()
    await expect(page.getByRole('img', { name: 'QR code for your authenticator app' })).toBeVisible()
    await page.getByRole('button', { name: 'Cancel' }).click()
    await expect(setup).toBeVisible()
  }
})

test('a new key must be confirmed; cancelling keeps the current one', async ({ page }) => {
  await page.goto('/account')
  await page.getByRole('button', { name: 'New key' }).click()
  await expect(page.getByRole('alertdialog', { name: 'Make a new API key?' })).toBeVisible()
  await page.getByRole('button', { name: 'Cancel' }).click()
  await expect(page.getByRole('alertdialog')).toBeHidden()
  await expect(page.getByText('shown only this once')).toBeHidden()
})
