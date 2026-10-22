import { queryOptions, useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { api } from '@/lib/api'
import type { Message } from './types'

export interface RoomLanguage {
  code: string
  name: string
  own: string
}

/** A message as this reader reads it: its translation, or null when it is in their language already. */
export interface Translation {
  text: string | null
  same: boolean
}

export const roomLanguagesQuery = queryOptions({
  queryKey: ['rooms', 'languages'],
  queryFn: ({ signal }) => api<RoomLanguage[]>('/api/rooms/languages', { signal }),
  staleTime: Infinity,
})

const key = 'arena.room.readIn'

/** The language this reader reads rooms in (kept in their browser, for every room), or '' for each message as written. */
export function useReadIn(): [string, (code: string) => void] {
  const [code, setCode] = useState(() => {
    try {
      return localStorage.getItem(key) ?? ''
    } catch {
      return ''
    }
  })
  return [
    code,
    (next) => {
      setCode(next)
      try {
        if (next) localStorage.setItem(key, next)
        else localStorage.removeItem(key)
      } catch {
        // Not kept (a private window): it holds while the page is open.
      }
    },
  ]
}

/** The most recent messages translated at once: those on screen, as a room is read from its end. */
const MostAtOnce = 60

/**
 * Others' messages in the room, in this reader's language: translated once on the server for everyone reading in it, asked
 * again only for messages it has not seen.
 */
export function useRoomTranslations(roomId: string | undefined, language: string, messages: Message[], me: string | undefined) {
  const ids = language && roomId
    ? messages
        .filter((m) => m.content && ((m.role === 'user' && m.author?.id && m.author.id !== me) || (m.role === 'assistant' && m.status !== 'failed')))
        .slice(-MostAtOnce)
        .map((m) => m.id)
    : []
  const query = useQuery({
    queryKey: ['rooms', roomId, 'translations', language, ids.join(',')],
    queryFn: ({ signal }) => api<{ translations: ({ id: string } & Translation)[]; error: string | null }>(`/api/rooms/${roomId}/translations`, { body: { language, ids }, signal }),
    enabled: ids.length > 0,
    staleTime: Infinity,
    placeholderData: (previous) => previous,
  })
  const map = new Map<string, Translation>((query.data?.translations ?? []).map((t) => [t.id, { text: t.text, same: t.same }]))
  return { of: (id: string) => map.get(id), error: query.data?.error ?? null, loading: query.isFetching }
}
