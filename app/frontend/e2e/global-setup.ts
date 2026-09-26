import { request, type FullConfig } from '@playwright/test'
import { mkdirSync } from 'node:fs'

/** Signs in as the admin once, through the API, and saves the session for every test. */
export default async function globalSetup(config: FullConfig) {
  const baseURL = config.projects[0]!.use.baseURL!
  const password = process.env.E2E_PASSWORD
  if (!password) throw new Error('Set E2E_PASSWORD to the admin password (ADMIN_PASSWORD in stack/.env).')
  const ctx = await request.newContext({ baseURL, ignoreHTTPSErrors: true })
  const res = await ctx.post('/api/auth/login', {
    data: { userName: process.env.E2E_USER ?? 'admin', password, remember: false, redirect: '/' },
    headers: { 'X-Requested-With': 'e2e' },
  })
  if (!res.ok()) throw new Error(`sign-in failed: HTTP ${res.status()} ${await res.text()}`)
  mkdirSync('e2e/.auth', { recursive: true })
  await ctx.storageState({ path: 'e2e/.auth/state.json' })
  await ctx.dispose()
}
