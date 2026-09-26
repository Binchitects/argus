import { formatValue } from '@/lib/format'

export type ModelKind = 'language' | 'embedding' | 'reranker' | 'projector' | 'draft' | 'image' | 'adapter' | 'unknown'

/** What a GGUF file is, read from its header and tensors by the API. */
export interface ModelProfile {
  kind: ModelKind
  why: string | null
  note: string | null
  architecture: string | null
  name: string | null
  sizeLabel: string | null
  quant: string | null
  structure: 'dense' | 'moe' | null
  attention: 'full' | 'hybrid' | 'sliding' | 'recurrent' | null
  parameters: number | null
  activeParameters: number | null
  bitsPerWeight: number | null
  weightBytes: number
  layers: number | null
  attentionLayers: number | null
  experts: { count: number; used: number; shared: number; layers: number; bytes: number } | null
  trainedContext: number | null
  slidingWindow: number | null
  embeddingLength: number | null
  vocabSize: number | null
  kvBytesPerToken: Record<string, number>
  recurrentBytesPerSlot: number
  mtpLayers: number
  thinking: boolean | null
  tools: boolean | null
  sampling: { temperature: number | null; topP: number | null; topK: number | null; minP: number | null } | null
  ropeScaling: { type: string; factor: number | null; originalContext: number | null } | null
  canStretch: boolean
  projector: { vision: boolean; audio: boolean; projectionDim: number | null; type: string | null } | null
  approximate: boolean
}

export interface LibraryFile {
  path: string
  size: number
  parts: number
  profile: ModelProfile
  usedBy: string[]
}

export const kindLabel: Record<ModelKind, string> = {
  language: 'Language model',
  embedding: 'Embedding model',
  reranker: 'Reranker',
  projector: 'Vision projector',
  draft: 'Draft head',
  image: 'Image model',
  adapter: 'LoRA adapter',
  unknown: 'Unreadable',
}

export const attentionLabel = { hybrid: 'Hybrid attention', sliding: 'Sliding window', recurrent: 'Recurrent', full: null } as const

export const bytes = (b: number) => formatValue(b, 'bytes')
export const tokens = (n: number) => n.toLocaleString('en-US')

/** 26,900,000,000 -> "26.9B", 6,670,000,000 -> "6.7B", 600,000,000 -> "600M". */
export function params(n: number | null | undefined): string | null {
  if (!n) return null
  if (n >= 1e9) return `${(n / 1e9).toFixed(n >= 1e11 ? 0 : 1).replace(/\.0$/, '')}B`
  return `${Math.round(n / 1e6)}M`
}

/** One line for a card or a list: "Mixture of experts · 177B, 6.7B active · UD-IQ4_XS". */
export function summary(p: ModelProfile | null | undefined): string | null {
  if (!p) return null
  if (p.kind !== 'language') return kindLabel[p.kind]
  const size = params(p.parameters)
  const active = p.structure === 'moe' ? params(p.activeParameters) : null
  return [p.structure === 'moe' ? 'Mixture of experts' : 'Dense', size && (active ? `${size}, ${active} active` : size), attentionLabel[p.attention ?? 'full'], p.quant]
    .filter(Boolean)
    .join(' · ')
}

