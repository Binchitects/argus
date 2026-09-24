import { AxeBuilder } from '@axe-core/playwright'
import { expect, type Page, type TestInfo } from '@playwright/test'

/** CI runs without a model gateway (E2E_NO_GATEWAY=1): the API answers 502 for what needs it. */
export const noGateway = !!process.env.E2E_NO_GATEWAY

/**
 * Collects console errors, including Content-Security-Policy violations.
 * Ignored: expected 401s (the signed-out check), ERR_CERT_VERIFIER_CHANGED,
 * which an installed Chrome logs when it reloads its certificate store mid-run,
 * and without a gateway, its 502s.
 */
export function watchConsole(page: Page): string[] {
  const errors: string[] = []
  page.on('console', (m) => {
    const t = m.text()
    if (m.type() !== 'error' || t.includes('401') || t.includes('ERR_CERT_VERIFIER_CHANGED') || (noGateway && t.includes('502'))) return
    errors.push(`${t} (${m.location().url || 'no url'})`)
  })
  page.on('pageerror', (e) => errors.push(e.message))
  return errors
}

/** No serious or critical accessibility violations (axe, WCAG 2.1 AA). */
export async function expectAccessible(page: Page, info: TestInfo, label: string) {
  const result = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa']).analyze()
  const bad = result.violations.filter((v) => v.impact === 'serious' || v.impact === 'critical')
  if (bad.length) await info.attach(`axe-${label}.json`, { body: JSON.stringify(bad, null, 2), contentType: 'application/json' })
  expect(bad.map((v) => `${v.id}: ${v.help} (${v.nodes.map((n) => n.target.join(' ')).slice(0, 3).join(' | ')})`), `accessibility of ${label}`).toEqual([])
}

export async function setTheme(page: Page, theme: 'light' | 'dark') {
  await page.evaluate((t) => localStorage.setItem('theme', t), theme)
  await page.reload()
  await expect(page.locator('html')).toHaveClass(theme === 'dark' ? /dark/ : /^(?!.*dark)/)
}

/** A screenshot for reviewing the look, kept with the test's results. */
export async function screenshot(page: Page, info: TestInfo, name: string) {
  const path = info.outputPath(`${name}.png`)
  await page.screenshot({ path, fullPage: true })
  await info.attach(name, { path, contentType: 'image/png' })
}
