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

test('connect your tools: the address, the setups and the certificate are the real ones', async ({ page, baseURL }) => {
  await page.goto('/setup')
  await expect(page.getByRole('heading', { level: 1, name: 'Connect your tools' })).toBeVisible()
  const url = new URL(baseURL!)
  const gateway = `${url.protocol}//gateway.${url.host}`
  await expect(page.getByText(`${gateway}/v1`, { exact: true })).toBeVisible()
  await expect(page.getByText(/export ANTHROPIC_BASE_URL=/)).toContainText(`ANTHROPIC_BASE_URL=${gateway}`)
  const cert = await page.request.get('/api/account/certificate')
  const offered = page.getByRole('link', { name: /The CA certificate/ })
  if (!(await (await page.request.get('/api/account/connect')).json()).certificate) {
    // No certificate exported (the app without its proxy, as in CI): nothing is offered.
    await expect(offered).toHaveCount(0)
    expect(cert.status()).toBe(404)
    return
  }
  // The CA offered signs the certificate the stack serves, and the bundle carries it with the public CAs.
  await expect(offered).toBeVisible()
  expect(cert.status()).toBe(200)
  const pem = await cert.text()
  expect(pem).toContain('BEGIN CERTIFICATE')
  const bundle = await (await page.request.get('/api/account/certificate?bundle=true')).text()
  expect(bundle).toContain(pem.trim().split('\n')[1])
  expect(bundle.match(/BEGIN CERTIFICATE/g)!.length).toBeGreaterThan(10)
})
