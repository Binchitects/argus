import { mkdirSync, writeFileSync } from 'node:fs'
import { request, type FullConfig } from '@playwright/test'

/** Signs in once through Authelia when credentials are given; otherwise the app is reached directly. */
export default async function globalSetup(config: FullConfig) {
  const baseURL = config.projects[0].use.baseURL!
  mkdirSync('e2e/.auth', { recursive: true })
  const password = process.env.E2E_PASSWORD
  if (!password) {
    writeFileSync('e2e/.auth/state.json', JSON.stringify({ cookies: [], origins: [] }))
    return
  }
  const app = new URL(baseURL)
  const auth = `${app.protocol}//auth.${app.host}`
  const ctx = await request.newContext({ ignoreHTTPSErrors: true })
  const res = await ctx.post(`${auth}/api/firstfactor`, {
    data: { username: process.env.E2E_USER ?? 'admin', password, keepMeLoggedIn: false, targetURL: baseURL },
  })
  if (!res.ok()) throw new Error(`Sign-in through ${auth} failed: HTTP ${res.status()}`)
  await ctx.storageState({ path: 'e2e/.auth/state.json' })
  await ctx.dispose()
}
