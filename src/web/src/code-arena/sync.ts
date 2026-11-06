import { useQueryClient } from '@tanstack/react-query'
import { useEffect, useRef } from 'react'
import { toast } from '@/components/ui/toaster'
import { configQuery } from '@/pages/chat/api'
import { stateQuery, type CodeState, type SyncState } from './api'
import { api } from './http'

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

/** How long the page waits before it asks again when code-arena did not answer whether Arena's settings changed. */
const settingsRetry = 5_000

/**
 * Arena's settings as code-arena applies them while it runs (the models, the thinking default, the tools, when it compacts,
 * the skills): code-arena answers the page's ask once they changed, and the page reads its config, state and skills
 * again then, so a change made in Arena shows within seconds.
 */
export function useArenaSettings() {
  const queryClient = useQueryClient()
  useEffect(() => {
    const stop = new AbortController()
    void (async () => {
      let seen: number | null = null
      while (!stop.signal.aborted) {
        try {
          const said: { version: number } = await api(`/api/settings/changed?after=${seen ?? -1}`, { signal: stop.signal })
          const version = said.version
          if (seen !== null && version !== seen)
            for (const queryKey of [configQuery.queryKey, stateQuery.queryKey, ['code', 'skills']]) void queryClient.invalidateQueries({ queryKey })
          seen = version
        } catch {
          if (stop.signal.aborted) return
          await new Promise((r) => setTimeout(r, settingsRetry))
        }
      }
    })()
    return () => stop.abort()
  }, [queryClient])
}
