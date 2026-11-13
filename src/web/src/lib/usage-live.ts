import { useQueryClient, type QueryKey } from '@tanstack/react-query'
import { useEffect, useRef } from 'react'

/** A page asks again every this long when the live stream is down (a proxy that cuts it): the stream is how it keeps up. */
export const liveFallback = 60_000

/**
 * Use as it is booked (the server's /api/usage/live: a request in the ledger, a message's time): the queries
 * <paramref name="keys"/> gives for a beat are read again at once, so Accounting, a project's numbers and Usage keep up
 * as requests end. A beat names the projects whose use changed; <paramref name="keys"/> returns nothing to let one pass.
 * The browser opens the stream again by itself when it drops.
 */
export function useUsageLive(keys: (projects: string[]) => QueryKey[], enabled = true) {
  const queryClient = useQueryClient()
  const keysRef = useRef(keys)
  useEffect(() => {
    keysRef.current = keys
  })
  useEffect(() => {
    if (!enabled || typeof EventSource === 'undefined') return
    const source = new EventSource('/api/usage/live')
    source.onmessage = (m) => {
      try {
        const e = JSON.parse(m.data as string) as { type?: string; projects?: string[] }
        if (e.type !== 'usage') return
        for (const queryKey of keysRef.current(e.projects ?? [])) void queryClient.invalidateQueries({ queryKey, refetchType: 'active' })
      } catch {
        // Not a beat.
      }
    }
    return () => source.close()
  }, [enabled, queryClient])
}
