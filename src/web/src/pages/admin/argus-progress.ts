/** How far an Argus index pass is: the whole pass, and each repository in it. */

/** How far a running pass is, as Argus's index process reports it. */
export interface IndexProgress {
  repos: number
  position: number
  repo?: string
  branch?: string
  done?: number
  total?: number | null
  stage: string
  what?: string
  outcomes: Record<string, string>
}

/** A pass's progress as one number, 0–100: the repositories done, and of the one on now its files. */
export function passPercent(p: IndexProgress | null | undefined): number {
  if (!p || !p.repos) return 0
  if (p.stage === 'finishing') return 99
  const current = p.total ? (p.done ?? 0) / p.total : 0
  return Math.min(99, Math.round((100 * (Math.max(0, p.position - 1) + current)) / p.repos))
}

/** Where one repository is in the run: indexing (with its files), queued, done this pass, or nothing. */
export function repoProgress(repo: string, running: boolean, p: IndexProgress | null | undefined, pending: string[]):
  | { state: 'indexing'; branch: string; percent: number | null }
  | { state: 'queued' }
  | { state: 'done' }
  | null {
  if (running && p?.repo === repo && p.stage !== 'finishing') {
    return { state: 'indexing', branch: p.branch ?? '', percent: p.total ? Math.round((100 * (p.done ?? 0)) / p.total) : null }
  }
  if (pending.includes(repo)) return { state: 'queued' }
  if (running && p && Object.keys(p.outcomes).some((k) => k.startsWith(`${repo}@`))) return { state: 'done' }
  return null
}
