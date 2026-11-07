import { api } from '@/lib/api'

/** A workspace's port as the Ports panel shows it (Arena's own API, not the workspace's engine). */
export interface ForwardedPort {
  port: number
  label: string | null
  /** Its own address: https://ws-<workspace>-<port>.<domain>/ */
  url: string
  visibility: 'private' | 'signed-in'
  /** Found listening (auto), or forwarded by hand (user). */
  origin: 'auto' | 'user'
  listening: boolean
  stopped: boolean
  process: string | null
  pid: number | null
  address: string | null
}

export interface PortsView {
  available: boolean
  running?: boolean
  reason?: string
  ports: ForwardedPort[]
}

export const portsQuery = (workspace: string) => ({
  queryKey: ['studio-ports', workspace] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<PortsView>(`/api/studio/workspaces/${workspace}/ports`, { signal }),
})

export interface PortChange {
  label?: string
  visibility?: ForwardedPort['visibility']
  stopped?: boolean
}

export const setPort = (workspace: string, port: number, change: PortChange) =>
  api<ForwardedPort>(`/api/studio/workspaces/${workspace}/ports/${port}`, { method: 'PUT', body: change })

export const removePort = (workspace: string, port: number) => api<void>(`/api/studio/workspaces/${workspace}/ports/${port}`, { method: 'DELETE' })

/** The port's name as VS Code shows it: its label, with the number. */
export const portName = (p: Pick<ForwardedPort, 'port' | 'label'>) => (p.label ? `${p.label} (${p.port})` : String(p.port))

/** A port typed in Forward a Port: a whole number from 1 to 65535 (also "localhost:3000"), else null. */
export function parsePort(text: string): number | null {
  const m = /^(?:(?:localhost|127\.0\.0\.1|\[::1\]):)?(\d{1,5})$/i.exec(text.trim())
  const n = m ? Number(m[1]) : NaN
  return Number.isInteger(n) && n >= 1 && n <= 65535 ? n : null
}
