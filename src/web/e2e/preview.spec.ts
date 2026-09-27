import { expect, test, type Page } from '@playwright/test'

type FromRunner = { type: string; message?: string; uri?: string }

/**
 * Runs code in the preview runner the way the chat does: a sandboxed frame of
 * /preview.html, sent the code once it is ready. Resolves with what the runner
 * reported once it drew (or failed), and a moment after for late reports.
 */
async function preview(page: Page, kind: string, code: string): Promise<FromRunner[]> {
  return page.evaluate(
    ({ kind, code }) =>
      new Promise<FromRunner[]>((resolve) => {
        document.getElementById('pv')?.remove()
        const frame = document.createElement('iframe')
        frame.id = 'pv'
        frame.src = '/preview.html'
        frame.setAttribute('sandbox', 'allow-scripts allow-forms allow-modals')
        frame.style.cssText = 'position:fixed;inset:0;width:800px;height:600px;z-index:9999;background:#fff'
        const seen: FromRunner[] = []
        let settle: ReturnType<typeof setTimeout> | undefined
        window.addEventListener('message', (e) => {
          if (e.source !== frame.contentWindow) return
          seen.push(e.data)
          if (e.data.type === 'ready') frame.contentWindow!.postMessage({ type: 'render', kind, code, theme: 'light' }, '*')
          if (e.data.type === 'rendered' || e.data.type === 'error') {
            clearTimeout(settle)
            settle = setTimeout(() => resolve(seen), 1500)
          }
        })
        setTimeout(() => resolve(seen), 20_000)
        document.body.append(frame)
      }),
    { kind, code },
  )
}

test.beforeEach(async ({ page }) => {
  await page.goto('/')
  await expect(page.getByRole('heading', { level: 1 })).toBeVisible()
})

test('a page runs its script, and Tailwind comes from here rather than the CDN', async ({ page }) => {
  const seen = await preview(
    page,
    'html',
    `<!doctype html><html><head><script src="https://cdn.tailwindcss.com"></script></head>
     <body><h1 id="t" class="text-red-600">waiting</h1><script>document.getElementById('t').textContent = 'ran: ' + (1 + 1)</script></body></html>`,
  )
  const frame = page.frameLocator('#pv')
  await expect(frame.getByRole('heading', { name: 'ran: 2' })).toBeVisible()
  await expect(frame.getByRole('heading')).toHaveCSS('color', /oklch\(0\.577 0\.245 27\.325\)|rgb\(220, 38, 38\)/)
  expect(seen.filter((m) => m.type === 'blocked')).toEqual([])
})

test('a page has no network, and none of the app: not its API, not its cookies', async ({ page }) => {
  const seen = await preview(
    page,
    'html',
    `<!doctype html><html><head></head><body><p id="r">...</p><script>
      const out = []
      try { out.push('cookie:' + document.cookie) } catch (e) { out.push('cookie:' + e.name) }
      try { localStorage.getItem('theme'); out.push('storage:open') } catch (e) { out.push('storage:' + e.name) }
      Promise.allSettled([fetch('/api/account/me'), fetch('https://example.com/')]).then((r) => {
        out.push('fetch:' + r.map((x) => x.status).join(','))
        document.getElementById('r').textContent = out.join(' ')
      })
    </script></body></html>`,
  )
  const result = page.frameLocator('#pv').locator('#r')
  await expect(result).toHaveText('cookie:SecurityError storage:SecurityError fetch:rejected,rejected')
  expect(seen.filter((m) => m.type === 'blocked').map((m) => m.uri)).toEqual(expect.arrayContaining([expect.stringContaining('example.com')]))
})

test('a React component with state, an icon and Tailwind classes', async ({ page }) => {
  const seen = await preview(
    page,
    'react',
    `import { useState } from 'react'
     import { Heart } from 'lucide-react'
     export default function App() {
       const [n, setN] = useState(0)
       return <button className="rounded bg-blue-600 px-3 py-1 text-white" onClick={() => setN(n + 1)}><Heart aria-hidden="true" /> Liked {n}</button>
     }`,
  )
  expect(seen.map((m) => m.type)).toContain('rendered')
  const button = page.frameLocator('#pv').getByRole('button', { name: 'Liked 0' })
  await button.click()
  await expect(page.frameLocator('#pv').getByRole('button', { name: 'Liked 1' })).toBeVisible()
  await expect(page.frameLocator('#pv').getByRole('button').locator('svg')).toHaveCount(1)
  await expect(page.frameLocator('#pv').getByRole('button')).toHaveCSS('color', 'rgb(255, 255, 255)')
})

test('a component that needs a library the preview lacks says which', async ({ page }) => {
  const seen = await preview(page, 'react', "import { LineChart } from 'recharts'\nexport default () => <LineChart />")
  expect(seen.find((m) => m.type === 'error')?.message).toMatch(/"recharts" cannot be loaded here/)
  await expect(page.frameLocator('#pv').getByText(/"recharts" cannot be loaded here/)).toBeVisible()
})

test('a picture and a diagram', async ({ page }) => {
  await preview(page, 'svg', '<svg xmlns="http://www.w3.org/2000/svg" width="120" height="60" role="img" aria-label="Logo"><rect width="120" height="60" fill="teal"/></svg>')
  await expect(page.frameLocator('#pv').getByRole('img', { name: 'Logo' })).toBeVisible()

  const seen = await preview(page, 'mermaid', 'graph TD\n  Start --> Finish')
  expect(seen.map((m) => m.type)).toContain('rendered')
  await expect(page.frameLocator('#pv').locator('svg')).toBeVisible()
  await expect(page.frameLocator('#pv').getByText('Finish')).toBeVisible()
})

test('the runner cannot be framed by another site', async ({ request }) => {
  const res = await request.get('/preview.html')
  expect(res.status()).toBe(200)
  const csp = res.headers()['content-security-policy']
  expect(csp).toContain('sandbox allow-scripts')
  expect(csp).toContain("connect-src 'none'")
  expect(csp).toContain("frame-ancestors 'self'")
  expect(csp).not.toContain('allow-same-origin')
})
