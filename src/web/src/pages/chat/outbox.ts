import { useEffect, useState } from 'react'
import type { SkillUse } from '@/lib/skills'
import type { Quote } from './quote'
import type { Attachment } from './types'

/** A message sent with no connection: it waits here (in the browser, for every tab) and is sent when the connection is back. */
export interface Outgoing {
  key: string
  /** Its chat; null: a new chat, made when it is sent. */
  chatId: string | null
  text: string
  attachments: Attachment[]
  quote?: Quote
  skill?: SkillUse
  at: number
  /** Sent with the connection back, and refused: it waits for the person (send again, or remove). */
  failed?: boolean
}

const storeKey = 'arena.outbox'

function read(): Outgoing[] {
  try {
    const kept = JSON.parse(localStorage.getItem(storeKey) ?? '[]') as unknown
    return Array.isArray(kept) ? (kept as Outgoing[]) : []
  } catch {
    return []
  }
}

function write(list: Outgoing[]) {
  try {
    if (list.length) localStorage.setItem(storeKey, JSON.stringify(list))
    else localStorage.removeItem(storeKey)
  } catch {
    // Not kept (a private window): it waits while the page is open.
  }
}

/** The browser says there is no connection. */
export const isOffline = () => typeof navigator !== 'undefined' && navigator.onLine === false

/** Whether the browser has a connection, as it changes. */
export function useOnline() {
  const [online, setOnline] = useState(() => !isOffline())
  useEffect(() => {
    const up = () => setOnline(true)
    const down = () => setOnline(false)
    window.addEventListener('online', up)
    window.addEventListener('offline', down)
    return () => {
      window.removeEventListener('online', up)
      window.removeEventListener('offline', down)
    }
  }, [])
  return online
}

/** The messages waiting to be sent: this chat's (or the new chat's), oldest first. */
export function useOutbox(chatId: string | null) {
  const [all, setAll] = useState<Outgoing[]>(read)
  // Another tab sent one or added one.
  useEffect(() => {
    const on = (e: StorageEvent) => e.key === storeKey && setAll(read())
    window.addEventListener('storage', on)
    return () => window.removeEventListener('storage', on)
  }, [])
  const save = (next: Outgoing[]) => {
    write(next)
    setAll(next)
  }
  return {
    here: all.filter((o) => o.chatId === chatId).sort((a, b) => a.at - b.at),
    add: (o: Omit<Outgoing, 'key' | 'at'>) => save([...read(), { ...o, key: `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`, at: Date.now() }]),
    remove: (key: string) => save(read().filter((o) => o.key !== key)),
    failed: (o: Outgoing) => save([...read().filter((x) => x.key !== o.key), { ...o, failed: true }]),
    retry: (key: string) => save(read().map((o) => (o.key === key ? { ...o, failed: false } : o))),
    /** The new chat was made: what waited for it goes to it. */
    adopt: (id: string) => save(read().map((o) => (o.chatId === null ? { ...o, chatId: id } : o))),
  }
}
