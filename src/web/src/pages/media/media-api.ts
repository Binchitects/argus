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

export type MediaAction = 'make' | 'edit' | 'vary' | 'enlarge' | 'move' | 'cutout' | 'speak' | 'music' | 'upload'

export interface MediaItem {
  id: string
  kind: 'picture' | 'video' | 'sound'
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
  action: Exclude<MediaAction, 'upload'>
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
}

export const mediaConfigQuery = queryOptions({ queryKey: ['media', 'config'], queryFn: ({ signal }) => api<MediaConfig>('/api/media', { signal }) })

export type Shown = 'all' | 'picture' | 'video' | 'sound'

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
  upload: 'Uploaded',
}

export const working: Record<MediaJob['action'], string> = {
  make: 'Drawing',
  edit: 'Changing',
  vary: 'Varying',
  enlarge: 'Enlarging',
  move: 'Making a clip',
  cutout: 'Taking the background away',
  speak: 'Speaking',
  music: 'Making music',
}
