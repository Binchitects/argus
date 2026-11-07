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

/**
 * A model moving to another engine at runtime (Llm.Core ModelMove): draining (its answers on the old engine end first, on
 * the same GPUs), loading on the new one, ready there (its last answers on the old one ending), or failed (back on the old one).
 */
export interface ModelMove {
  phase: 'draining' | 'loading' | 'ready' | 'failed'
  /** The kinds it leaves and goes to, and those engines by name. */
  from: string
  to: string
  fromEngine: string
  toEngine: string
  /** Whether its old engine still has it: serving, stopping or gone. */
  old: 'serving' | 'stopping' | 'gone'
  since: string
  at: string
  by: string | null
  /** Why it failed. */
  error: string | null
}

/** A saved model with what only some engines have (Llm.Core EngineKinds). */
export type EngineModel = SavedModel & {
  /** Its move to another engine, underway or failed; null: none. */
  move?: ModelMove | null
  engineKind?: ChatEngineKind
  engine?: string | null
  replicas?: number
  gpuMemoryUtilization?: number | null
  quantization?: string | null
  specDecode?: string | null
  kvCpuGb?: number | null
  kvTier?: 'cpu' | 'lmcache' | null
  kvDiskGb?: number | null
  sleep?: boolean
  fallback?: string | null
}

/** A Hugging Face snapshot in the library, for vLLM and SGLang. */
export interface Snapshot {
  path: string
  repo: string
  revision: string
  size: number
  complete: boolean
  why: string | null
  architectures: string[]
  /** The precision its weights are stored at, as its config says: "NVFP4", "MXFP4", "FP8", "BF16"... */
  precision?: string | null
  usedBy: string[]
}

/** An engine instance (Admin → Models' engine.instances), and why its models could not be listed. */
export interface EngineInstance {
  name: string
  kind: string
  managed: boolean
  problem: string | null
  /** False for this stack's own engine whose feature is off (deploy/features): it runs nothing. */
  on?: boolean
}

/** The kinds of engine a chat model runs on (Llm.Core EngineKinds). */
export type ChatEngineKind = 'llamacpp' | 'vllm' | 'sglang' | 'lmdeploy' | 'strata'

/** The kinds that run one server a model, run by the supervisor. */
export type ServerKind = Exclude<ChatEngineKind, 'llamacpp'>

/**
 * What the form needs of each kind that runs one server a model (EngineKinds' capabilities): the files it runs, whether the
 * app sets its speculative decoding, tiers beyond the GPU and sleep, its copies, and its own flags' names for the hints.
 */
export const serverKinds: Record<ServerKind, {
  label: string
  snapshot: boolean
  gguf: boolean
  speculative: boolean
  tiers: boolean
  sleep: boolean
  replicas: boolean
  /** A quantisation the app may set on top of its checkpoint's. */
  quantization: boolean
  /** Its flag for the share of each GPU, for people at once, and the most people at once. */
  share: string
  parallel: string
  maxParallel: number
}> = {
  vllm: { label: 'vLLM', snapshot: true, gguf: true, speculative: true, tiers: true, sleep: true, replicas: true, quantization: true, share: '--gpu-memory-utilization', parallel: '--max-num-seqs', maxParallel: 1024 },
  sglang: { label: 'SGLang', snapshot: true, gguf: true, speculative: true, tiers: true, sleep: true, replicas: true, quantization: true, share: '--mem-fraction-static', parallel: '--max-running-requests', maxParallel: 1024 },
  lmdeploy: { label: 'LMDeploy', snapshot: true, gguf: false, speculative: false, tiers: false, sleep: false, replicas: true, quantization: false, share: '--cache-max-entry-count, of what is free once its weights are in', parallel: '--max-batch-size', maxParallel: 1024 },
  strata: { label: 'Strata', snapshot: false, gguf: true, speculative: false, tiers: false, sleep: false, replicas: false, quantization: false, share: 'what of the GPUs it fills; the experts beyond it stay in RAM', parallel: '--parallel', maxParallel: 8 },
}

/** The kinds that run one server a model. */
export const serverPerModel = (kind: string | undefined): kind is ServerKind => !!kind && kind in serverKinds

/** A kind's name for people. */
export const engineLabel = (kind: string | undefined) => (serverPerModel(kind) ? serverKinds[kind].label : 'llama.cpp')

/** The snapshots in the library (vLLM's and SGLang's models). */
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
