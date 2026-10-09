import { useEffect } from 'react'
import { toast } from '@/components/ui/toaster'

/** Said once when deep research is taken from someone who had it. */
export const researchGone = {
  title: 'Deep research is no longer available to you',
  description: 'An admin decides who may use it (Admin → Tools). Your messages go as plain ones.',
}

// Where the mark lives when the browser keeps nothing (a private window): this page only.
const memory = new Set<string>()

function had(key: string): boolean {
  try {
    return localStorage.getItem(key) === '1'
  } catch {
    return memory.has(key)
  }
}

function mark(key: string, on: boolean) {
  try {
    if (on) localStorage.setItem(key, '1')
    else localStorage.removeItem(key)
  } catch {
    if (on) memory.add(key)
    else memory.delete(key)
  }
}

/**
 * The deep research switch shows only for people an admin gave it to (Admin → Tools). When an
 * admin turns it off, or gives it to others only, it goes from the composer of someone who had
 * it, and this says why, once: at once while the page is open, or the next time they open the
 * chat on this browser. Remembered per person, so someone else signing in here is not told.
 */
export function useResearchGone(person: string | undefined, can: boolean) {
  useEffect(() => {
    const key = `research-had:${person ?? ''}`
    if (can) {
      mark(key, true)
    } else if (had(key)) {
      mark(key, false)
      toast(researchGone.title, { id: 'research-gone', description: researchGone.description })
    }
  }, [person, can])
}
