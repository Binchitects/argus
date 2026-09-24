import { mkdirSync } from 'node:fs'
import { request, type FullConfig } from '@playwright/test'

/** Signs in once as the admin, through the app's own sign-in; every test starts from that session. */
export default async function globalSetup(config: FullConfig) {
  const baseURL = config.projects[0].use.baseURL!
  const password = process.env.E2E_PASSWORD
  if (!password) throw new Error('Set E2E_PASSWORD to the admin password (ADMIN_PASSWORD in .env).')
  mkdirSync('e2e/.auth', { recursive: true })
  const ctx = await request.newContext({ baseURL, ignoreHTTPSErrors: true })
  const res = await ctx.post('/api/auth/login', {
    headers: { 'X-Requested-With': 'e2e' },
    data: { userName: process.env.E2E_USER ?? 'admin', password },
  })
  if (!res.ok()) throw new Error(`Admin sign-in at ${baseURL} failed: HTTP ${res.status()} ${await res.text()}`)
  await ctx.storageState({ path: 'e2e/.auth/state.json' })
  await ctx.dispose()
}
