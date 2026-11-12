import { AxeBuilder } from '@axe-core/playwright'
import { expect, type Page, type TestInfo } from '@playwright/test'

/** CI runs without a model behind the gateway (E2E_NO_ENGINE=1): the API answers 502 for what needs one. */
export const noEngine = !!process.env.E2E_NO_ENGINE

/** CI runs without Prometheus, Loki and Alertmanager (E2E_NO_OBSERVE=1): the Logs, Alerts and metric panels have nothing to read. */
export const noObserve = !!process.env.E2E_NO_OBSERVE

/**
 * Collects console errors, including Content-Security-Policy violations.
 * Ignored: expected 401s (the signed-out check), ERR_CERT_VERIFIER_CHANGED,
 * which an installed Chrome logs when it reloads its certificate store mid-run,
 * the service worker's script refused over the stack's own certificate (a
 * browser registers one only over a certificate it trusts; ignoreHTTPSErrors
 * does not reach it), and without a gateway, its 502s. Nor what the test runner
 * itself is refused in a frame that runs no script (a drawing's stage: Playwright
 * puts its own script in every frame; a plain browser logs nothing there).
 */
export function watchConsole(page: Page): string[] {
  const errors: string[] = []
  page.on('console', (m) => {
    const t = m.text()
    // The service worker's script, its fetch cut short (the test went on to the next page, or Chrome reloaded its
    // certificates): Chrome says so itself, and the app goes on without the worker until the next page.
    if (m.type() !== 'error' || t.includes('401') || t.includes('ERR_CERT_VERIFIER_CHANGED') || t.includes('SSL certificate error occurred when fetching the script') ||
      t.includes('An unknown error occurred when fetching the script') || t.startsWith("Blocked script execution in 'about:srcdoc'") || (noEngine && t.includes('502'))) return
    errors.push(`${t} (${m.location().url || 'no url'})`)
  })
  page.on('pageerror', (e) => errors.push(e.message))
  return errors
}

/**
 * No serious or critical accessibility violations (axe, WCAG 2.2 AA). Frames that run no script (a drawing's sandboxed
 * stage) are left out: axe cannot run in them, and waits for them.
 */
export async function expectAccessible(page: Page, info: TestInfo, label: string, scriptless: string[] = []) {
  // Every stylesheet loaded: one the browser failed leaves the page unstyled, which axe would report as low contrast.
  // Chrome gives a failed one a sheet all the same, whose rules cannot be read (the app's own can).
  const unloaded = () =>
    page.evaluate(() =>
      [...document.querySelectorAll<HTMLLinkElement>('link[rel=stylesheet]')]
        .filter((l) => {
          try {
            return !l.sheet?.cssRules.length
          } catch {
            return true
          }
        })
        .map((l) => l.href),
    )
  await expect.poll(unloaded, { message: `the stylesheets of ${label} loaded` }).toEqual([])
  // A dialog fading in has text at part opacity: axe reads its contrast once it has arrived.
  await page.evaluate(() =>
    Promise.race([
      Promise.all(document.getAnimations().filter((a) => a.effect?.getTiming().iterations !== Infinity).map((a) => a.finished.catch(() => undefined))),
      new Promise((resolve) => setTimeout(resolve, 2000)),
    ]),
  )
  const axe = new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa'])
  for (const frame of scriptless) axe.exclude(frame)
  const result = await axe.analyze()
  const bad = result.violations.filter((v) => v.impact === 'serious' || v.impact === 'critical')
  if (bad.length) await info.attach(`axe-${label}.json`, { body: JSON.stringify(bad, null, 2), contentType: 'application/json' })
  expect(bad.map((v) => `${v.id}: ${v.help} (${v.nodes.map((n) => n.target.join(' ')).slice(0, 3).join(' | ')})`), `accessibility of ${label}`).toEqual([])
}

/**
 * The theme for every page this test opens, set before any page loads: no reload
 * in the middle of a page's requests. A colour theme is kept in the browser and
 * said to be the account's for this page only (the admin's own is left as it is:
 * the tests run at once).
 */
export async function withTheme(page: Page, theme: 'light' | 'dark', colorTheme?: string) {
  // Init scripts run in every frame: a sandboxed preview has no storage to set.
  await page.addInitScript(([t, c]) => {
    if (window !== window.top) return
    localStorage.setItem('theme', t)
    if (c) localStorage.setItem('color-theme', c)
  }, [theme, colorTheme ?? ''])
  if (colorTheme) {
    await page.route('**/api/auth/me', async (route) => {
      const response = await route.fetch()
      await route.fulfill({ response, json: { ...(await response.json()), colorTheme } })
    })
  }
}

export async function expectTheme(page: Page, theme: 'light' | 'dark', colorTheme?: string) {
  await expect(page.locator('html')).toHaveClass(theme === 'dark' ? /dark/ : /^(?!.*dark)/)
  if (colorTheme) await expect(page.locator('html')).toHaveAttribute('data-color-theme', colorTheme)
}

/** A screenshot for reviewing the look, kept with the test's results. */
export async function screenshot(page: Page, info: TestInfo, name: string) {
  const path = info.outputPath(`${name}.png`)
  await page.screenshot({ path, fullPage: true })
  await info.attach(name, { path, contentType: 'image/png' })
}
