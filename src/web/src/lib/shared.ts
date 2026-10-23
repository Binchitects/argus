/** What another app shared to Arena (the installed app's share target): kept by the service worker until the chat takes it. */
export const sharedCache = 'shared-v1'

export interface Shared {
  text: string
  files: File[]
}

interface SharedMeta {
  title: string
  text: string
  url: string
  files: { name: string; type: string }[]
}

/** The words of a share: its title, text and link, each once (apps often put the link in the text too). */
export function sharedText(meta: Pick<SharedMeta, 'title' | 'text' | 'url'>): string {
  let parts: string[] = []
  for (const p of [meta.title, meta.text, meta.url].map((x) => (x ?? '').trim()).filter(Boolean)) {
    if (parts.some((q) => q.includes(p))) continue
    parts = [...parts.filter((q) => !p.includes(q)), p]
  }
  return parts.join('\n\n')
}

/** The share the service worker kept under this id, taken (it is removed); null when there is none. */
export async function takeShared(id: string): Promise<Shared | null> {
  if (typeof caches === 'undefined' || !/^[a-z0-9-]{1,40}$/.test(id)) return null
  const cache = await caches.open(sharedCache)
  const found = await cache.match(`/shared/${id}`)
  if (!found) return null
  const meta = (await found.json()) as SharedMeta
  const files = await Promise.all(
    meta.files.map(async (f, i) => {
      const body = await cache.match(`/shared/${id}/${i}`)
      return body ? new File([await body.blob()], f.name, { type: f.type }) : null
    }),
  )
  await Promise.all([cache.delete(`/shared/${id}`), ...meta.files.map((_, i) => cache.delete(`/shared/${id}/${i}`))])
  return { text: sharedText(meta), files: files.filter((f): f is File => f !== null) }
}
