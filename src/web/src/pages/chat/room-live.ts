import { useQueryClient } from '@tanstack/react-query'
import { useEffect, useState } from 'react'
import { conversationQuery, watchRoom, type RoomEvent } from './api'
import type { RoomBrief } from './types'

/**
 * A room's live events while its page is open: the chat is read again when someone writes, an answer starts or ends, or
 * its people or settings change; who is here and who is typing. Returns who is here and the names typing now.
 */
export function useRoomEvents(chatId: string | undefined, room: RoomBrief | null | undefined, me: string) {
  const queryClient = useQueryClient()
  const [here, setHere] = useState<string[]>([])
  const [typing, setTyping] = useState<Record<string, number>>({})
  const isRoom = !!room
  useEffect(() => {
    if (!chatId || !isRoom) return
    const abort = new AbortController()
    const on = (e: RoomEvent) => {
      if (e.type === 'presence') setHere(e.here)
      else if (e.type === 'typing') {
        if (e.by !== me) setTyping((t) => ({ ...t, [e.by]: Date.now() + 6000 }))
      } else {
        if (e.type === 'message' || e.type === 'answering') setTyping({})
        void queryClient.invalidateQueries({ queryKey: conversationQuery(chatId).queryKey })
      }
    }
    // The events stream ends now and then (a proxy, a restart): it is opened again, while the page is open.
    const loop = async () => {
      while (!abort.signal.aborted) {
        try {
          await watchRoom(chatId, on, abort.signal)
        } catch {
          // closed, or taken out of the room: the next read of the chat says so
        }
        if (!abort.signal.aborted) await new Promise((r) => setTimeout(r, 3000))
      }
    }
    void loop()
    return () => abort.abort()
  }, [chatId, isRoom, me, queryClient])
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => {
    if (Object.keys(typing).length === 0) return
    const t = setInterval(() => setNow(Date.now()), 1000)
    return () => clearInterval(t)
  }, [typing])
  const typingNames = Object.entries(typing)
    .filter(([, until]) => until > now)
    .map(([id]) => room?.members.find((m) => m.id === id)?.name ?? 'Someone')
  return { here, typingNames }
}

/** "Sara is typing…", "Sara and Ben are typing…". */
export function typingLine(names: string[]) {
  if (names.length === 0) return null
  if (names.length === 1) return `${names[0]} is typing…`
  return `${names.slice(0, -1).join(', ')} and ${names.at(-1)} are typing…`
}
