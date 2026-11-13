import { api } from '@/lib/api'

export interface PlantSite {
  id: string
  name: string
  description: string | null
  timeZone: string
  groupIds: string[]
  createdAt: string
  updatedAt: string
  assets: number
  points: number
}

export interface PlantKinds {
  areas: string[]
  assets: string[]
  types: string[]
  sources: string[]
}

export interface PlantOverview {
  sites: PlantSite[]
  mayManage: boolean
  kinds: PlantKinds
}

export interface PlantArea {
  id: string
  parentId: string | null
  name: string
  kind: string
  position: number
}

export interface PlantAsset {
  id: string
  siteId: string
  areaId: string | null
  name: string
  kind: string
  vendor: string | null
  model: string | null
  serial: string | null
  description: string | null
  tags: string[]
  x: number | null
  y: number | null
  points: number
}

export interface PlantPoint {
  id: string
  assetId: string
  name: string
  description: string | null
  dataType: string
  unit: string | null
  source: string
  address: string | null
  lowLow: number | null
  low: number | null
  high: number | null
  highHigh: number | null
  deadband: number | null
  states: string | null
  /** Its latest value, as the gateway last sent it. */
  latest: { at: string; value: number | null; text: string | null } | null
}

export interface PlantSiteWhole {
  site: PlantSite
  areas: PlantArea[]
  assets: PlantAsset[]
}

export interface PlantImported {
  areas: number
  assets: number
  points: number
  sheetLines: number
}

export const plantQuery = {
  queryKey: ['plant'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<PlantOverview>('/api/plant', { signal }),
}

export const plantSiteQuery = (id: string) => ({
  queryKey: ['plant', 'site', id] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<PlantSiteWhole>(`/api/plant/sites/${id}`, { signal }),
})

export const plantAssetQuery = (id: string) => ({
  queryKey: ['plant', 'asset', id] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<{ asset: PlantAsset; points: PlantPoint[] }>(`/api/plant/assets/${id}`, { signal }),
})

/** The areas as a tree: each with its depth, a parent before its children, in their order. */
export function areaTree(areas: PlantArea[]): { area: PlantArea; depth: number }[] {
  const children = new Map<string | null, PlantArea[]>()
  for (const a of areas) {
    const key = a.parentId && areas.some((p) => p.id === a.parentId) ? a.parentId : null
    children.set(key, [...(children.get(key) ?? []), a])
  }
  const out: { area: PlantArea; depth: number }[] = []
  const seen = new Set<string>()
  const walk = (parent: string | null, depth: number) => {
    for (const a of children.get(parent) ?? []) {
      if (seen.has(a.id)) continue
      seen.add(a.id)
      out.push({ area: a, depth })
      walk(a.id, depth + 1)
    }
  }
  walk(null, 0)
  return out
}

/** A point's limits in a few words: "low 10 · high 80 °C". */
export function limits(p: PlantPoint): string {
  const parts = [
    p.lowLow != null && `low-low ${p.lowLow}`,
    p.low != null && `low ${p.low}`,
    p.high != null && `high ${p.high}`,
    p.highHigh != null && `high-high ${p.highHigh}`,
  ].filter(Boolean)
  return parts.length ? parts.join(' · ') + (p.unit ? ` ${p.unit}` : '') : ''
}

/** A point's latest value in words: "21.5 °C", "on", "running" (from its states), or its text. */
export function shown(p: PlantPoint): string {
  const l = p.latest
  if (!l) return ''
  if (p.dataType === 'text') return l.text ?? ''
  if (l.value == null) return ''
  if (p.dataType === 'boolean') return l.value ? 'on' : 'off'
  if (p.dataType === 'state' && p.states) {
    const named = p.states
      .split(',')
      .map((s) => s.split('='))
      .find(([k]) => Number(k.trim()) === l.value)
    if (named?.[1]) return named[1].trim()
  }
  return `${Number(l.value.toPrecision(6))}${p.unit ? ` ${p.unit}` : ''}`
}

export interface PlantBucket {
  at: string
  avg: number | null
  min: number | null
  max: number | null
  last: number | null
  n: number
}

export interface PlantSeries {
  point: string
  from: string
  to: string
  bucketSeconds: number
  buckets?: PlantBucket[]
  texts?: { at: string; text: string | null }[]
}

export const plantSeriesQuery = (id: string, from: Date, to: Date, buckets = 300) => ({
  queryKey: ['plant', 'series', id, from.toISOString(), to.toISOString(), buckets] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) =>
    api<PlantSeries>(`/api/plant/points/${id}/series?${new URLSearchParams({ from: from.toISOString(), to: to.toISOString(), buckets: String(buckets) })}`, { signal }),
})

/** The chart's unit for a point's own (°C, W, kWh, %), or none: the unit is said in the chart's title then. */
export function chartUnit(unit: string | null): string | undefined {
  switch (unit?.trim()) {
    case '°C':
    case 'C':
      return 'celsius'
    case 'W':
      return 'watt'
    case 'kWh':
      return 'kwatth'
    case '%':
      return 'percent'
    default:
      return undefined
  }
}

/** A series as the time chart draws it: the average a bucket, with its lowest and highest as a band. */
export function trendSeries(name: string, s: PlantSeries): { name: string; points: (number | null)[][]; range: number[][] }[] {
  const b = s.buckets ?? []
  return [
    {
      name,
      points: b.map((x) => [Date.parse(x.at), x.avg]),
      range: b.filter((x) => x.min != null && x.max != null).map((x) => [Date.parse(x.at), x.min!, x.max!]),
    },
  ]
}
