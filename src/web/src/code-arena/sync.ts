import { useEffect, useRef } from 'react'
import { toast } from '@/components/ui/toaster'
import type { CodeState, SyncState } from './api'

/** The sync in words, as /sync says it in the terminal. */
export function syncWords(sync: SyncState): string {
  const whose = sync.account ? ` (${sync.account}'s chats)` : ''
  if (sync.trouble) return `Not in step with Arena: ${sync.trouble}.${sync.waiting > 0 ? ` ${sync.waiting} to send.` : ''}`
  if (!sync.chat) return `Kept with Arena${whose}: its chat is made with the first message.`
  const waiting = sync.waiting > 0 ? ` ${sync.waiting} message${sync.waiting === 1 ? '' : 's'} still to send.` : ' Arena has every message.'
  return `Kept in step with its chat in Arena${whose}.${waiting}${sync.behind ? ' The web chat has new messages: they come in at the next turn.' : ''}`
}

/**
 * What code-arena said while nothing ran (Arena out of reach, the web's news taken in): each shown once, as it comes. Those
 * already there when the page opened are shown when they are recent (the last minute).
 */
export function useNotices(state?: CodeState) {
  const seen = useRef<number | null>(null)
  useEffect(() => {
    const notices = state?.notices ?? []
    const first = seen.current === null
    const since = first ? Date.now() - 60_000 : 0
    for (const n of notices) {
      if ((seen.current !== null && n.id <= seen.current) || (first && Date.parse(n.at) < since)) continue
      toast.info(n.text)
    }
    seen.current = Math.max(seen.current ?? 0, ...notices.map((n) => n.id))
  }, [state?.notices])
}
