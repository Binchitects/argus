import { expect, test } from '@playwright/test'
import { expectAccessible, watchConsole } from './helpers.ts'

const headers = { 'X-Requested-With': 'e2e' }
// With a real model behind the gateway (the deployed stack): E2E_CHAT=1.
const live = process.env.E2E_CHAT === '1'

/** An app that uses window.arena: who is using it, its store, and (live) the model. */
const app = (ask: boolean) => `<!doctype html><html><body>
<h1>Bridge check</h1><p id="u">...</p><p id="s">storing</p><p id="o">${ask ? 'asking' : 'not asked'}</p>
<script>
  document.getElementById('u').textContent = 'User: ' + arena.user.name
  arena.store.set('count', { n: 7 }).then(() => arena.store.get('count')).then((v) => (document.getElementById('s').textContent = 'Stored: ' + v.n))
  ${ask ? "arena.ask('Name one colour. One word.', { system: 'Answer in one word.' }).then((t) => (document.getElementById('o').textContent = 'Answer: ' + t), (e) => (document.getElementById('o').textContent = 'Error: ' + e.message))" : ''}
</script></body></html>`

test('an app runs at its link: it knows who uses it, keeps its data on the server, and asks the model', async ({ page }, info) => {
  const errors = watchConsole(page)
  // Its own name: the desktop and mobile runs, at the same time, see each other's apps.
  const name = `Bridge check ${info.project.name} ${Math.random().toString(36).slice(2, 8)}`
  const made = await page.request.post('/api/apps', { data: { name, description: 'An e2e check.', kind: 'html', code: app(live), reach: 'private' }, headers })
  expect(made.status()).toBe(201)
  const id = (await made.json()).id as string
  try {
    await page.goto(`/apps/${id}`)
    await expect(page.getByRole('heading', { level: 1, name })).toBeVisible()
    const frame = page.frameLocator(`iframe[title="Preview of ${name}"]`)
    await expect(frame.getByText(/^User: \S/)).toBeVisible()
    await expect(frame.getByText('Stored: 7')).toBeVisible()
    // The store is the server's, not the page's.
    const kept = await page.request.get(`/api/apps/${id}/data/count`)
    expect((await kept.json()).value).toEqual({ n: 7 })
    if (live) await expect(frame.getByText(/^Answer: \S/)).toBeVisible({ timeout: 120_000 })
    await expectAccessible(page, info, 'app')

    await page.goto('/apps')
    await page.getByRole('radio', { name: 'Mine' }).click()
    await expect(page.getByRole('list', { name: 'Apps' }).getByRole('link', { name: new RegExp(name) })).toBeVisible()
    expect(errors).toEqual([])
  } finally {
    await page.request.delete(`/api/apps/${id}`, { headers })
  }
})
