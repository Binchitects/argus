import { defineConfig, devices } from '@playwright/test'

/**
 * Code Arena's page in a real browser, laid out as people see it, with no stack: Vite serves the page (as
 * vite.code-arena.config.ts builds it) and each test answers its API itself (page.route).
 *   npx playwright test --config playwright.ide.config.ts
 * E2E_CHANNEL=chrome uses the installed Google Chrome instead of Playwright's own browser. E2E_IDE_PORT serves it on
 * another port: two checkouts running these at once would otherwise share one server (the other's code).
 */
const port = Number(process.env.E2E_IDE_PORT) || 5179

export default defineConfig({
  testDir: './e2e-ide',
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  reporter: 'list',
  use: {
    baseURL: `http://127.0.0.1:${port}`,
    channel: process.env.E2E_CHANNEL || undefined,
    contextOptions: { reducedMotion: 'reduce' },
    trace: 'retain-on-failure',
  },
  webServer: {
    command: `node_modules/.bin/vite --config vite.code-arena.config.ts --host 127.0.0.1 --port ${port} --strictPort`,
    url: `http://127.0.0.1:${port}/code-arena.html`,
    reuseExistingServer: !process.env.CI,
    timeout: 120_000,
  },
  projects: [{ name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1400, height: 900 } } }],
})
