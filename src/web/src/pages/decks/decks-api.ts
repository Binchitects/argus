import { api } from '@/lib/api'
import type { DataResult } from '../data/data-api'

export type Layout = 'title' | 'section' | 'bullets' | 'two' | 'chart' | 'picture' | 'quote'

export interface DeckChart {
  type: 'bar' | 'line'
  categories: string[]
  series: { name: string; values: (number | null)[] }[]
  /** The question asked of a database for its numbers. */
  ask?: { source: string; question: string } | null
  /** Where its numbers came from, or why there are none. */
  note?: string | null
}

export interface Slide {
  layout: Layout
  title?: string | null
  subtitle?: string | null
  bullets?: string[] | null
  left?: string[] | null
  right?: string[] | null
  quote?: string | null
  by?: string | null
  chart?: DeckChart | null
  picture?: { prompt: string; fileId?: string | null } | null
  notes?: string | null
}

export interface Deck {
  id: string
  title: string
  templateId: string | null
  version: number
  createdAt: string
  updatedAt: string
  slides: Slide[]
}

export interface DeckBrief {
  id: string
  title: string
  templateId: string | null
  version: number
  updatedAt: string
  slides: number
  first: Slide | null
}

/** A template's look as the browser draws it: its theme's colours and fonts. */
export interface DeckTheme {
  colors?: Record<string, string>
  fonts?: { major?: string | null; minor?: string | null }
  width?: number
  height?: number
}

export interface DeckTemplate {
  id: string
  name: string
  isDefault: boolean
  theme: DeckTheme
}

export interface DeckConfig {
  pictures: boolean
  templates: DeckTemplate[]
  sources: string[]
}

export interface DeckVersion {
  version: number
  title: string
  note: string
  createdAt: string
}

export const decksQuery = {
  queryKey: ['decks'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<DeckBrief[]>('/api/decks', { signal }),
}

export const deckConfigQuery = {
  queryKey: ['decks', 'config'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<DeckConfig>('/api/decks/config', { signal }),
}

export const deckQuery = (id: string) => ({
  queryKey: ['decks', id] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<Deck>(`/api/decks/${id}`, { signal }),
})

export const deckVersionsQuery = (id: string) => ({
  queryKey: ['decks', id, 'versions'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<DeckVersion[]>(`/api/decks/${id}/versions`, { signal }),
})

export const makeDeck = (body: { about: string; templateId?: string }) => api<Deck>('/api/decks', { body })

export const changeDeck = (id: string, body: { change: string; slide?: number }) => api<Deck>(`/api/decks/${id}/ask`, { body })

export const editDeck = (id: string, body: { title?: string; slides?: Slide[]; templateId?: string; note?: string }) => api<Deck>(`/api/decks/${id}`, { method: 'PUT', body })

export const removeDeck = (id: string) => api(`/api/decks/${id}`, { method: 'DELETE' })

export const makePicture = (id: string, index: number) => api<Deck>(`/api/decks/${id}/slides/${index}/picture`, { body: {} })

export const refreshDeck = (id: string) => api<Deck>(`/api/decks/${id}/refresh`, { body: {} })

export const restoreDeck = (id: string, version: number) => api<Deck>(`/api/decks/${id}/versions/${version}/restore`, { body: {} })

/** The built-in look's template id, when choosing one. */
export const BuiltIn = '00000000-0000-0000-0000-000000000000'

export const layoutLabels: Record<Layout, string> = {
  title: 'Title',
  section: 'Section',
  bullets: 'Points',
  two: 'Two columns',
  chart: 'Chart',
  picture: 'Picture',
  quote: 'Quote',
}

/** A deck chart as a query result, for the data chart: a column of names, a column for each series. */
export function chartResult(chart: DeckChart): DataResult {
  return {
    columns: [{ name: 'Category', type: 'text' }, ...chart.series.map((s) => ({ name: s.name, type: 'number' as const }))],
    rows: chart.categories.map((c, i) => [c, ...chart.series.map((s) => s.values[i] ?? null)]),
    truncated: false,
    milliseconds: 0,
  }
}
