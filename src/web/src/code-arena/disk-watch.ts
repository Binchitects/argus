import { useEffect, useRef } from 'react'
import { diskChanges, type DiskChanges } from './ide-api'

/**
 * Files changed on disk by anyone (the person's own editor, a command, git) reach the page: `onChanges` is given what
 * changed, as code-arena's watcher tells it, or null when everything may have (the page came back into view, the watcher
 * lost track, or there is none). One request waits on the server at a time; a failed one is tried again a little later.
 */
export function useDiskChanges(onChanges: (changes: DiskChanges | null) => void) {
  const handler = useRef(onChanges)
  useEffect(() => {
    handler.current = onChanges
  }, [onChanges])

  useEffect(() => {
    const stop = new AbortController()
    let watching = true
    void (async () => {
      let since = 0
      while (!stop.signal.aborted) {
        try {
          const d = await diskChanges(since, stop.signal)
          watching = d.watching
          // The first answer only says where to start.
          if (since > 0 && (d.all || d.changes.length > 0)) handler.current(d.all ? null : d)
          since = d.version
        } catch {
          if (stop.signal.aborted) return
          await new Promise((r) => setTimeout(r, 5_000))
        }
      }
    })()
    // Back into view: what changed meanwhile is read again where nothing watches, or what the watcher may have missed.
    let hidden = document.visibilityState === 'hidden'
    const back = () => {
      const was = hidden
      hidden = document.visibilityState === 'hidden'
      if (was && !hidden && !watching) handler.current(null)
    }
    const focused = () => {
      if (!watching) handler.current(null)
    }
    document.addEventListener('visibilitychange', back)
    window.addEventListener('focus', focused)
    return () => {
      stop.abort()
      document.removeEventListener('visibilitychange', back)
      window.removeEventListener('focus', focused)
    }
  }, [])
}
