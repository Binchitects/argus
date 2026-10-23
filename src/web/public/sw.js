// The installable app's service worker: the app's shell when offline, the bell's news
// pushed while no page is open, and what another app shares to Arena (kept until the
// chat takes it). Never the API: /api, /connect and /.well-known always go to the
// network, and their answers are never kept.
const SHELL = 'shell-v1'
const SHARED = 'shared-v1'
// The most a share keeps (its files together): more is left out.
const MAX_SHARED_BYTES = 50 * 1024 * 1024
// Fingerprinted files kept at most; the oldest go first (old releases' files).
const MAX_ASSETS = 300

self.addEventListener('install', (event) => {
  event.waitUntil(
    caches
      .open(SHELL)
      .then((cache) => cache.addAll(['/', '/favicon.svg', '/theme-init.js', '/icons/icon-192.png']))
      .then(() => self.skipWaiting()),
  )
})

self.addEventListener('activate', (event) => {
  event.waitUntil(
    caches
      .keys()
      .then((keys) => Promise.all(keys.filter((k) => k !== SHELL && k !== SHARED).map((k) => caches.delete(k))))
      .then(() => self.clients.claim()),
  )
})

async function keep(request, response) {
  const cache = await caches.open(SHELL)
  await cache.put(request, response)
  const keys = (await cache.keys()).filter((r) => new URL(r.url).pathname.startsWith('/assets/'))
  for (const old of keys.slice(0, Math.max(0, keys.length - MAX_ASSETS))) await cache.delete(old)
}

// A share from another app (the manifest's share_target): its words and files kept, and a new chat opened to take them.
async function share(request) {
  const form = await request.formData()
  const id = `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`
  const cache = await caches.open(SHARED)
  // Shares nobody took (the page never opened) go after a day.
  for (const old of await cache.keys()) {
    const at = parseInt(new URL(old.url).pathname.split('/')[2] || '', 36)
    if (!at || Date.now() - at > 86_400_000) await cache.delete(old)
  }
  const files = []
  let bytes = 0
  for (const f of form.getAll('files')) {
    if (typeof f === 'string' || f.size === 0 || bytes + f.size > MAX_SHARED_BYTES) continue
    bytes += f.size
    files.push(f)
  }
  await Promise.all(files.map((f, i) => cache.put(`/shared/${id}/${i}`, new Response(f, { headers: { 'Content-Type': f.type || 'application/octet-stream' } }))))
  const meta = {
    title: String(form.get('title') || ''),
    text: String(form.get('text') || ''),
    url: String(form.get('url') || ''),
    files: files.map((f, i) => ({ name: f.name || `shared-${i + 1}`, type: f.type || 'application/octet-stream' })),
  }
  await cache.put(`/shared/${id}`, new Response(JSON.stringify(meta), { headers: { 'Content-Type': 'application/json' } }))
  return Response.redirect(`/chat?shared=${id}`, 303)
}

self.addEventListener('fetch', (event) => {
  const request = event.request
  const url = new URL(request.url)
  if (request.method === 'POST' && url.origin === self.location.origin && url.pathname === '/share') {
    event.respondWith(share(request).catch(() => Response.redirect('/chat', 303)))
    return
  }
  if (request.method !== 'GET') return
  if (url.origin !== self.location.origin || /^\/(api|connect|\.well-known)(\/|$)/.test(url.pathname)) return
  if (request.mode === 'navigate') {
    // A page: the network first (always the latest release), the kept shell when offline.
    event.respondWith(
      fetch(request)
        .then((response) => {
          if (response.ok) {
            const copy = response.clone()
            event.waitUntil(caches.open(SHELL).then((cache) => cache.put('/', copy)))
          }
          return response
        })
        .catch(() => caches.match('/')),
    )
    return
  }
  if (url.pathname.startsWith('/assets/')) {
    // Fingerprinted by the build: a file never changes, so a kept one is as good as the network's.
    event.respondWith(
      caches.match(request).then(
        (hit) =>
          hit ??
          fetch(request).then((response) => {
            if (response.ok) event.waitUntil(keep(request, response.clone()))
            return response
          }),
      ),
    )
  }
})

self.addEventListener('push', (event) => {
  let news = {}
  try {
    news = event.data ? event.data.json() : {}
  } catch {
    news = { title: event.data ? event.data.text() : '' }
  }
  event.waitUntil(
    self.clients.matchAll({ type: 'window', includeUncontrolled: true }).then((windows) => {
      // A page in front shows it in the bell already (and as a toast).
      if (windows.some((w) => w.visibilityState === 'visible' && w.focused)) return
      return self.registration.showNotification(news.title || 'Argus Arena', {
        body: news.body || undefined,
        tag: news.tag || undefined,
        icon: '/icons/icon-192.png',
        badge: '/icons/icon-192.png',
        data: { link: news.link || '/' },
      })
    }),
  )
})

self.addEventListener('notificationclick', (event) => {
  event.notification.close()
  const link = new URL((event.notification.data && event.notification.data.link) || '/', self.location.origin)
  // Only the app's own pages.
  if (link.origin !== self.location.origin) return
  event.waitUntil(
    self.clients.matchAll({ type: 'window', includeUncontrolled: true }).then((windows) => {
      const open = windows.find((w) => new URL(w.url).origin === self.location.origin)
      if (open) return open.focus().then((w) => (w && 'navigate' in w ? w.navigate(link.href) : self.clients.openWindow(link.href)))
      return self.clients.openWindow(link.href)
    }),
  )
})
