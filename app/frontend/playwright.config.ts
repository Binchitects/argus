import { defineConfig, devices } from '@playwright/test'

/**
 * Browser tests against a running web + API, signed in as the admin:
 *   E2E_BASE_URL=https://llm.localhost E2E_PASSWORD=... npm run e2e
 * E2E_CHANNEL=chrome uses the installed Google Chrome instead of Playwright's own
 * browser (for machines where `npx playwright install` cannot download).
 * One retry everywhere: an installed Chrome reloads its certificate store now and
 * then, failing every request in flight (net::ERR_FAILED, measured; nothing reaches
 * the server). A test that passes on its retry is still reported as "flaky".
 */
export default defineConfig({
  testDir: './e2e',
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: 1,
  reporter: process.env.CI ? [['list'], ['html', { open: 'never' }]] : 'list',
  globalSetup: './e2e/global-setup.ts',
  use: {
    baseURL: process.env.E2E_BASE_URL ?? 'https://llm.localhost',
    ignoreHTTPSErrors: true,
    storageState: 'e2e/.auth/state.json',
    trace: 'retain-on-failure',
    channel: process.env.E2E_CHANNEL || undefined,
    // Axe and the screenshots see pages at rest, not halfway through a fade
    // (the app turns its motion off for prefers-reduced-motion).
    contextOptions: { reducedMotion: 'reduce' },
  },
  projects: [
    { name: 'desktop', use: { ...devices['Desktop Chrome'] } },
    { name: 'mobile', use: { ...devices['Pixel 7'] } },
  ],
})
