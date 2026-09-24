import type { Page } from '@playwright/test'

/**
 * Fails the test on any console error, including Content-Security-Policy violations.
 * Ignored: expected 401s (the signed-out check) and ERR_CERT_VERIFIER_CHANGED, which
 * an installed Chrome logs when it reloads its certificate store mid-run.
 */
export function watchConsole(page: Page): string[] {
  const errors: string[] = []
  page.on('console', (m) => m.type() === 'error' && !m.text().includes('401') && !m.text().includes('ERR_CERT_VERIFIER_CHANGED') && errors.push(m.text()))
  page.on('pageerror', (e) => errors.push(e.message))
  return errors
}
