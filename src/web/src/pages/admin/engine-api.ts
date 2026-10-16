import { useQuery } from '@tanstack/react-query'
import { api } from '@/lib/api'
import type { SavedModel } from './model-form'

/** A GPU as the engines see it: its index, name, memory, vendor and backend. */
export interface EngineGpu {
  index: number
  name: string
  total: number
  vendor?: string
  backend?: string
}

/** A saved model with what only some engines have (Llm.Core EngineKinds). */
export type EngineModel = SavedModel & {
  engineKind?: 'llamacpp' | 'vllm'
  engine?: string | null
  replicas?: number
  gpuMemoryUtilization?: number | null
  quantization?: string | null
  specDecode?: string | null
  kvCpuGb?: number | null
  sleep?: boolean
  fallback?: string | null
}

/** A Hugging Face snapshot in the library, for vLLM. */
export interface Snapshot {
  path: string
  repo: string
  revision: string
  size: number
  complete: boolean
  why: string | null
  architectures: string[]
  usedBy: string[]
}

/** An engine instance (Admin → Models' engine.instances), and why its models could not be listed. */
export interface EngineInstance {
  name: string
  kind: string
  managed: boolean
  problem: string | null
}

/** The snapshots in the library (vLLM's models). */
export function useSnapshots() {
  return useQuery({
    queryKey: ['admin', 'models', 'library', 'snapshots'],
    queryFn: ({ signal }) => api<Snapshot[]>('/api/admin/models/library/snapshots', { signal }),
    retry: false,
  })
}

/** The model names a fallback may be: every chat model at the gateway but this one. */
export function useFallbacks(self: string | undefined) {
  const models = useQuery({
    queryKey: ['admin', 'models'],
    queryFn: ({ signal }) => api<{ models: { name: string; mode: string }[] }>('/api/admin/models', { signal }),
  })
  return (models.data?.models ?? []).filter((m) => m.mode === 'chat' && m.name !== self).map((m) => m.name)
}
