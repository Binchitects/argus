import { infiniteQueryOptions, queryOptions } from '@tanstack/react-query'
import { api } from '@/lib/api'

export interface MediaConfig {
  /** The picture model this person may use here; null: none (the studio cannot make pictures). */
  picture: string | null
  video: string | null
  /** Whether pictures can be enlarged (the picture server has its upscaler). */
  enlarge: boolean
  /** The studio server's: a picture's background taken away, speech in a voice from a sample, music. */
  cutout: boolean
  voice: boolean
  music: boolean
  going: number
  mostGoing: number
}

export type MediaAction = 'make' | 'edit' | 'vary' | 'enlarge' | 'move' | 'cutout' | 'speak' | 'music' | 'story' | 'draw' | 'trace' | 'reshape' | 'animate' | 'export' | 'upload'

export interface MediaItem {
  id: string
  kind: 'picture' | 'video' | 'sound' | 'vector'
  action: MediaAction
  prompt: string | null
  parentId: string | null
  width: number | null
  height: number | null
  seconds: number | null
  model: string | null
  createdAt: string
  attachmentId: string
  fileName: string
  contentType: string
  size: number
  /** It has a small JPEG for the gallery. */
  thumb: boolean
}

export interface MediaJob {
  id: string
  action: Exclude<MediaAction, 'upload' | 'story'>
  prompt: string | null
  sourceId: string | null
  count: number
  createdAt: string
  finishedAt: string | null
  status: 'working' | 'done' | 'failed'
  phase: string | null
  error: string | null
  items: string[]
}

export interface MediaJobRequest {
  action: MediaJob['action']
  prompt?: string
  sourceId?: string
  count?: number
  width?: number
  height?: number
  strength?: number
  seconds?: number
  /** A painted change: the picture as drawn here, and where it was painted (white), both PNG data URLs of the same size. */
  picture?: string
  mask?: string
  lyrics?: string
  /** Speak in the voice of this sound file of the person's, with their word that they may (consent). */
  voiceId?: string
  consent?: boolean
  exaggeration?: number
  style?: string
  trace?: string
  format?: string
  side?: number
  fps?: number
  background?: string
  length?: number
  /** Draw and trace: moving as its words say, gently alive when they say not (always); only when they say (auto); still (never). */
  animate?: 'always' | 'auto' | 'never'
}

export const mediaConfigQuery = queryOptions({ queryKey: ['media', 'config'], queryFn: ({ signal }) => api<MediaConfig>('/api/media', { signal }) })

export type Shown = 'all' | 'picture' | 'video' | 'sound' | 'vector'

export const mediaItemsQuery = (kind: Shown) =>
  infiniteQueryOptions({
    queryKey: ['media', 'items', kind],
    queryFn: ({ signal, pageParam }) => {
      const q = new URLSearchParams()
      if (kind !== 'all') q.set('kind', kind)
      if (pageParam) q.set('before', pageParam)
      return api<{ items: MediaItem[]; more: boolean }>(`/api/media/items?${q}`, { signal })
    },
    initialPageParam: '',
    getNextPageParam: (last) => (last.more ? last.items.at(-1)?.createdAt : undefined),
  })

/** While work goes on, asked again every second and a half. */
export const mediaJobsQuery = queryOptions({
  queryKey: ['media', 'jobs'],
  queryFn: ({ signal }) => api<MediaJob[]>('/api/media/jobs', { signal }),
  refetchInterval: (q) => (q.state.data?.some((j) => j.status === 'working') ? 1500 : false),
})

export const startJob = (body: MediaJobRequest) => api<MediaJob>('/api/media/jobs', { body })
export const addUpload = (attachmentId: string) => api<{ id: string }>('/api/media/items', { body: { attachmentId } })
export const removeItem = (id: string) => api<void>(`/api/media/items/${id}`, { method: 'DELETE' })

export const contentUrl = (item: Pick<MediaItem, 'attachmentId'>) => `/api/chat/attachments/${item.attachmentId}/content`
export const thumbUrl = (item: Pick<MediaItem, 'id' | 'thumb' | 'attachmentId'>) => (item.thumb ? `/api/media/items/${item.id}/thumb` : contentUrl(item))

/** A size the picture model takes: at most 1024 pixels a side (its shape kept), at least 64, each a multiple of 16 (as the server). */
export function fit(width: number, height: number): { width: number; height: number } {
  const scale = Math.min(1, 1024 / Math.max(width, height))
  const side = (v: number) => Math.min(1024, Math.max(64, Math.floor((v * scale) / 16) * 16))
  return { width: side(width), height: side(height) }
}

/** How a piece of work is named on the page. */
export const actionNames: Record<MediaAction, string> = {
  make: 'Made from words',
  edit: 'Changed',
  vary: 'A variation',
  enlarge: 'Enlarged',
  move: 'Set moving',
  cutout: 'Background taken away',
  speak: 'Spoken',
  music: 'Music',
  story: 'A video',
  draw: 'Drawn as SVG',
  trace: 'Traced into SVG',
  reshape: 'Changed',
  animate: 'Animated',
  export: 'Exported',
  upload: 'Uploaded',
}

export interface StoryShot {
  visual: string
  narration: string
  seconds: number
  /** What was made for it, as file addresses: kept while what it came from holds. */
  picture?: string | null
  clip?: string | null
  speech?: string | null
}

export interface Story {
  id: string
  title: string
  brief: string
  voiceId: string | null
  music: string | null
  videoItemId: string | null
  /** The video's file address, once cut. */
  video: string | null
  status: 'draft' | 'making' | 'done' | 'failed'
  phase: string | null
  error: string | null
  createdAt: string
  updatedAt: string
  shots: StoryShot[]
}

export const storiesQuery = queryOptions({
  queryKey: ['media', 'stories'],
  queryFn: ({ signal }) => api<Story[]>('/api/media/stories', { signal }),
  refetchInterval: (q) => (q.state.data?.some((s) => s.status === 'making') ? 3000 : false),
})

export const storyQuery = (id: string) =>
  queryOptions({
    queryKey: ['media', 'stories', id],
    queryFn: ({ signal }) => api<Story>(`/api/media/stories/${id}`, { signal }),
    refetchInterval: (q) => (q.state.data?.status === 'making' ? 2000 : false),
  })

export const writeStory = (brief: string, shots: number) => api<Story>('/api/media/stories', { body: { brief, shots } })
export const changeStory = (id: string, body: { title: string; shots: Pick<StoryShot, 'visual' | 'narration' | 'seconds'>[]; voiceId: string | null; consent: boolean; music: string | null }) =>
  api<Story>(`/api/media/stories/${id}`, { method: 'PUT', body })
export const makeStory = (id: string) => api<Story>(`/api/media/stories/${id}/make`, { method: 'POST' })
export const againShot = (id: string, index: number) => api<Story>(`/api/media/stories/${id}/shots/${index}/again`, { method: 'POST' })
export const removeStory = (id: string) => api<void>(`/api/media/stories/${id}`, { method: 'DELETE' })

export const working: Record<MediaJob['action'], string> = {
  make: 'Drawing',
  edit: 'Changing',
  vary: 'Varying',
  enlarge: 'Enlarging',
  move: 'Making a clip',
  cutout: 'Taking the background away',
  speak: 'Speaking',
  music: 'Making music',
  draw: 'Drawing',
  trace: 'Tracing',
  reshape: 'Changing the drawing',
  animate: 'Animating',
  export: 'Exporting',
}

/** A motion of a drawing's part (its id, or "scene"): a kind, its timing, and what the kind takes. */
export interface Motion {
  target: string
  kind: string
  duration?: number
  delay?: number
  stagger?: number
  repeat?: string
  alternate?: boolean
  easing?: string
  from?: string
  amount?: number
  origin?: string
  color?: string
  keys?: { at: number; x?: number; y?: number; scale?: number; rotate?: number; opacity?: number; fill?: string }[]
}

export interface VectorDrawing {
  id: string
  prompt: string | null
  width: number | null
  height: number | null
  /** The drawing as it stands still, made safe. */
  still: string
  motions: Motion[]
  /** The drawing as people take it: its motions compiled in. */
  markup: string
  seconds: number
  loops: boolean
  source: 'drawn' | 'traced' | 'uploaded' | string
  style: string | null
  version: number
  updatedAt: string
}

export interface VectorConfig {
  styles: { name: string; about: string }[]
  traces: { name: string; about: string }[]
  kinds: { kind: string; about: string }[]
  formats: string[]
  /** Animations can be recorded (the browser module runs). */
  moves: boolean
  tracesPictures: boolean
  drawsPictures: boolean
}

export interface PartBox {
  id: string
  tag: string
  x: number
  y: number
  width: number
  height: number
  fill: string | null
  text: string | null
}

export const vectorConfigQuery = queryOptions({ queryKey: ['media', 'vector-config'], queryFn: ({ signal }) => api<VectorConfig>('/api/media/vectors/config', { signal }), staleTime: 60_000 })
export const vectorQuery = (id: string) => queryOptions({ queryKey: ['media', 'vector', id], queryFn: ({ signal }) => api<VectorDrawing>(`/api/media/vectors/${id}`, { signal }) })
export const vectorVersionsQuery = (id: string) =>
  queryOptions({ queryKey: ['media', 'vector', id, 'versions'], queryFn: ({ signal }) => api<{ version: number; change: string; createdAt: string }[]>(`/api/media/vectors/${id}/versions`, { signal }) })
export const changeVector = (id: string, body: { still?: string; motions?: Motion[] }) =>
  api<{ drawing: VectorDrawing; problems: string[] }>(`/api/media/vectors/${id}`, { method: 'PUT', body })
export const restoreVersion = (id: string, version: number) => api<void>(`/api/media/vectors/${id}/versions/${version}/restore`, { method: 'POST' })
export const uploadVector = (svg: string, name: string) => api<{ id: string; changed: string[] }>('/api/media/vectors', { body: { svg, name } })
export const svgUrl = (id: string) => `/api/media/items/${id}/svg`

/** Why code typed by hand is not an SVG yet (what the browser's XML reader says), or null when it is one. */
export function notSvg(code: string): string | null {
  const doc = new DOMParser().parseFromString(code, 'image/svg+xml')
  const error = doc.querySelector('parsererror')
  if (error) return (error.textContent ?? 'Not XML').split('\n').map((l) => l.trim()).find((l) => l.length > 0 && !/^This page contains/.test(l)) ?? 'Not XML'
  return doc.documentElement.localName === 'svg' ? null : 'It does not start with <svg>.'
}
