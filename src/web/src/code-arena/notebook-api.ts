import { api } from './http'
import type { Output } from './notebook'

/** A notebook's kernel, as code-arena says: the Python it runs (or would), whether it runs or is busy, its count, and whether the folder is trusted. */
export interface KernelInfo {
  python: string
  running: boolean
  busy: boolean
  count: number
  trusted: boolean
}

export const kernelQuery = (path: string) => ({
  queryKey: ['code', 'notebook', 'kernel', path] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<KernelInfo>(`/api/notebook/kernel?path=${encodeURIComponent(path)}`, { signal }),
})

interface RunRead {
  version: number
  outputs: Output[]
  done: boolean
  ok: boolean
  count: number | null
}

/**
 * A cell run in the notebook's kernel: its outputs given as they come (all of them each time), to its end; whether it
 * went well, its execution count and how long it took.
 */
export async function runCell(path: string, code: string, onOutputs: (outputs: Output[], count: number | null) => void, signal?: AbortSignal): Promise<{ ok: boolean; count: number | null; took: number }> {
  const started = performance.now()
  const { run } = await api<{ run: string }>('/api/notebook/execute', { body: { path, code }, signal })
  let since = 0
  for (;;) {
    const read = await api<RunRead>(`/api/notebook/run?id=${run}&since=${since}`, { signal })
    if (read.version !== since || read.done) onOutputs(read.outputs, read.count)
    if (read.done) return { ok: read.ok, count: read.count, took: performance.now() - started }
    since = read.version
  }
}

export const interruptKernel = (path: string) => api<KernelInfo>('/api/notebook/interrupt', { body: { path } })
export const restartKernel = (path: string) => api<KernelInfo>('/api/notebook/restart', { body: { path } })
