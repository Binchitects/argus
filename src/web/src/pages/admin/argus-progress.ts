/** How far an Argus index run is: the whole run, and each repository in it. */

/** Where one repository is in the run going now, as Argus's index process reports it. */
export interface RepoProgress {
  state: 'queued' | 'fetching' | 'files' | 'symbols' | 'embedding' | 'done' | 'failed'
  branch?: string | null
  done?: number | null
  total?: number | null
  /** Unix seconds. */
  started?: number | null
  finished?: number | null
  /** ok, up_to_date, warning or failed. */
  outcome?: string | null
  /** How it ended, in a sentence. */
  message?: string | null
}

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
  by_repo?: Record<string, RepoProgress>
}

/** A pass's progress as one number, 0–100: the repositories done, and of the one on now its files. */
export function passPercent(p: IndexProgress | null | undefined): number {
  if (!p || !p.repos) return 0
  if (p.stage === 'finishing') return 99
  const current = p.total ? (p.done ?? 0) / p.total : 0
  return Math.min(99, Math.round((100 * (Math.max(0, p.position - 1) + current)) / p.repos))
}

/** Whether a repository is being worked on now (not waiting, not done). */
export const isWorking = (p: RepoProgress | null | undefined) => !!p && ['fetching', 'files', 'symbols', 'embedding'].includes(p.state)

/** Where a repository is, in words: "Reading files on main: 30 of 120". */
export function progressWords(p: RepoProgress): string {
  const on = p.branch ? ` on ${p.branch}` : ''
  switch (p.state) {
    case 'queued':
      return 'Waiting for its turn'
    case 'fetching':
      return 'Fetching from GitLab…'
    case 'files':
      return p.total ? `Reading files${on}: ${(p.done ?? 0).toLocaleString()} of ${p.total.toLocaleString()}` : `Reading files${on}…`
    case 'symbols':
      return p.total ? `Reading symbols${on} from ${p.total.toLocaleString()} ${p.total === 1 ? 'file' : 'files'}…` : `Reading symbols${on}…`
    case 'embedding':
      return p.total ? `Embedding for meaning search: ${(p.done ?? 0).toLocaleString()} of ${p.total.toLocaleString()}` : 'Embedding for meaning search…'
    case 'done':
      return p.message ?? 'Done'
    case 'failed':
      return p.message ?? 'Failed'
  }
}

/** How far a repository's current step is, 0–100; null when the step has no count. */
export function progressPercent(p: RepoProgress | null | undefined): number | null {
  if (!p || !(p.state === 'files' || p.state === 'embedding') || !p.total) return null
  return Math.min(100, Math.round((100 * (p.done ?? 0)) / p.total))
}
