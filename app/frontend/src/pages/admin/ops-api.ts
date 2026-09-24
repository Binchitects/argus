/** What an Argus index run's exit code means (src/argus/cli.py). */
export const indexExit: Record<string, string> = {
  '0': 'completed',
  '1': 'ran, but at least one repository is unhealthy; the log names it',
  '3': 'could not reach GitLab, or the token cannot list every repository',
  '4': 'preflight failed: ctags is missing or not Universal Ctags, or the include graph could not be rebuilt',
  '-1': 'Argus could not start the run at all',
}

/** https://grafana.llm.example.com for "grafana" (next.<domain> pages point at the real one). */
export function serviceUrl(sub: string, location: Pick<Location, 'protocol' | 'host'> = window.location): string {
  const host = location.host.startsWith('next.') ? location.host.slice(5) : location.host
  return `${location.protocol}//${sub}.${host}`
}

export interface Probe {
  name: string
  purpose: string
  ok: boolean
  detail: string
}

export interface IndexSummary {
  repos?: number
  stale?: number
  errored?: number
  never_run?: number
  files?: number
  symbols?: number
  stale_names?: string[]
  returncode?: number | null
  finished?: number | null
  state?: string
  error?: string
}

export interface Overview {
  people: number
  admins: number
  spend: number
  overCredit: string[]
  warning: string | null
  services: Probe[]
  index: { configured: boolean; summary: IndexSummary | null; error: string | null }
  model: string | null
}
