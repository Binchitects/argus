import { api } from '@/lib/api'

export interface ToolUsage {
  toolId: string
  calls: number
  failed: number
  averageMs: number
  lastError: { error: string | null; at: string } | null
  functions: { name: string; calls: number; failed: number; averageMs: number }[]
}

export const usageQuery = {
  queryKey: ['admin', 'tools', 'usage'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<ToolUsage[]>('/api/admin/tools/usage?days=7', { signal }),
}

export interface CustomToolRow {
  id: string
  toolId: string
  name: string
  kind: 'http' | 'prompt'
  title: string
  description: string
  parameters: { name: string; type: string; description: string | null; required: boolean }[]
  method: string | null
  urlTemplate: string | null
  headers: string[]
  bodyTemplate: string | null
  resultPath: string | null
  askFirst: boolean
  model: string | null
  promptTemplate: string | null
  systemPrompt: string | null
}

export const customToolsQuery = {
  queryKey: ['admin', 'tools', 'custom'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<CustomToolRow[]>('/api/admin/tools/custom', { signal }),
}
