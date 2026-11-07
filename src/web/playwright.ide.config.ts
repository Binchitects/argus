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
  // The page as people get it: built, then served as files. Vite's dev server compiles each module the first time a page
  // asks for it, so the first test in a worker on a cold machine (CI) waited for Monaco's compile and timed out where the
  // second did not. E2E_IDE_DEV=1 serves it from the dev server instead, for a quick try of a change.
  webServer: {
    command: process.env.E2E_IDE_DEV
      ? `node_modules/.bin/vite --config vite.code-arena.config.ts --host 127.0.0.1 --port ${port} --strictPort`
      : `node_modules/.bin/vite build --config vite.code-arena.config.ts --outDir .e2e-ide-dist --emptyOutDir --logLevel warn` +
        ` && node_modules/.bin/vite preview --config vite.code-arena.config.ts --outDir .e2e-ide-dist --host 127.0.0.1 --port ${port} --strictPort`,
    url: `http://127.0.0.1:${port}/code-arena.html`,
    reuseExistingServer: !process.env.CI,
    timeout: 300_000,
  },
  projects: [{ name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1400, height: 900 } } }],
})
