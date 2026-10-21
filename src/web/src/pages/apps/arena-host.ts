import { api } from '@/lib/api'

/** How the page around a preview or an app answers its window.arena calls. */
export type ArenaHandler = (method: string, args: Record<string, unknown>) => Promise<unknown>

const text = (v: unknown) => (typeof v === 'string' ? v : undefined)

/** Asked as the person using it: the model's answer as text. */
async function ask(path: string, args: Record<string, unknown>) {
  const r = await api<{ text: string }>(path, { body: { prompt: text(args.prompt) ?? '', system: text(args.system), model: text(args.model) } })
  return r.text
}

/**
 * A chat's preview, not published yet: ask goes to the model as the person, the store is kept in the page (until it
 * reloads), so the app works before it is published.
 */
export function previewArena(): ArenaHandler {
  const kept = new Map<string, unknown>()
  const key = (args: Record<string, unknown>) => `${args.mine === true ? 'mine' : 'all'}:${text(args.key) ?? ''}`
  return async (method, args) => {
    switch (method) {
      case 'ask':
        return ask('/api/apps/ask', args)
      case 'store.get':
        return kept.get(key(args)) ?? null
      case 'store.set':
        kept.set(key(args), structuredClone(args.value ?? null))
        return null
      case 'store.remove':
        kept.delete(key(args))
        return null
      case 'store.keys': {
        const prefix = args.mine === true ? 'mine:' : 'all:'
        return [...kept.keys()].filter((k) => k.startsWith(prefix)).map((k) => k.slice(prefix.length))
      }
      default:
        throw new Error(`There is no arena.${method}.`)
    }
  }
}

/** A published app: ask as the person using it, the store on the server (everyone's, or theirs with mine). */
export function appArena(appId: string): ArenaHandler {
  const data = (key: string, mine: boolean) => `/api/apps/${appId}/data/${encodeURIComponent(key)}${mine ? '?mine=true' : ''}`
  return async (method, args) => {
    const mine = args.mine === true
    switch (method) {
      case 'ask':
        return ask(`/api/apps/${appId}/ask`, args)
      case 'store.get':
        return (await api<{ value: unknown }>(data(text(args.key) ?? '', mine))).value ?? null
      case 'store.set':
        await api(data(text(args.key) ?? '', mine), { method: 'PUT', body: { value: args.value ?? null } })
        return null
      case 'store.remove':
        await api(data(text(args.key) ?? '', mine), { method: 'DELETE' })
        return null
      case 'store.keys':
        return (await api<{ key: string }[]>(`/api/apps/${appId}/data${mine ? '?mine=true' : ''}`)).map((d) => d.key)
      default:
        throw new Error(`There is no arena.${method}.`)
    }
  }
}
