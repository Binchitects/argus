import { api } from '@/lib/api'
import type { Attachment } from '@/pages/chat/types'

/** A meeting as the list shows it: its state while it is made, how long it was, and its action items (open of all). */
export interface MeetingBrief {
  id: string
  title: string
  status: 'processing' | 'ready' | 'failed'
  step: string | null
  error: string | null
  seconds: number | null
  createdAt: string
  actions: number
  open: number
}

/** A part of what was said: from when to when (seconds), by whom (a number from 0), what. */
export interface TranscriptLine {
  start: number
  end: number
  speaker: number
  text: string
}

export interface MeetingSpeaker {
  speaker: number
  name: string
  personId: string | null
  person: string | null
}

export interface MeetingAction {
  id: string
  number: number
  text: string
  speaker: number | null
  /** The name given when it is not a speaker's. */
  given: string | null
  /** Whose it is, in words: its speaker's name, or the name given. */
  owner: string | null
  personId: string | null
  person: string | null
  due: string | null
  done: boolean
}

export interface MeetingFull {
  meeting: MeetingBrief
  language: string | null
  summary: string | null
  model: string | null
  planId: string | null
  transcript: TranscriptLine[]
  speakers: MeetingSpeaker[]
  decisions: string[]
  actions: MeetingAction[]
}

export const meetingsQuery = {
  queryKey: ['meetings'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<MeetingBrief[]>('/api/meetings', { signal }),
  // While one is made: read again for its step and its end.
  refetchInterval: (q: { state: { data?: MeetingBrief[] } }) => (q.state.data?.some((m) => m.status === 'processing') ? 2000 : false),
}

export const meetingQuery = (id: string) => ({
  queryKey: ['meetings', id] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<MeetingFull>(`/api/meetings/${id}`, { signal }),
  refetchInterval: (q: { state: { data?: MeetingFull } }) => (q.state.data?.meeting.status === 'processing' ? 2000 : false),
})

export const meetingAudioUrl = (id: string) => `/api/meetings/${id}/audio`

/** A recording sent to be made a meeting (a sound, or a video's sound), with its title when given. */
export async function uploadMeeting(file: File, title?: string): Promise<MeetingBrief> {
  const form = new FormData()
  form.append('file', file)
  if (title?.trim()) form.append('title', title.trim())
  return api<MeetingBrief>('/api/meetings', { body: form })
}

export const renameMeeting = (id: string, title: string) => api<MeetingFull>(`/api/meetings/${id}`, { method: 'PUT', body: { title } })
export const deleteMeeting = (id: string) => api(`/api/meetings/${id}`, { method: 'DELETE' })
export const makeAgain = (id: string) => api<MeetingBrief>(`/api/meetings/${id}/again`, { body: {} })
export const nameSpeaker = (id: string, speaker: number, body: { name?: string; personId?: string | null }) =>
  api<MeetingFull>(`/api/meetings/${id}/speakers/${speaker}`, { method: 'PUT', body })

/** An action item's change: what is left out stays as it is. */
export interface ActionChange {
  text?: string
  speaker?: number
  owner?: string
  due?: string
  done?: boolean
  clearSpeaker?: boolean
  clearDue?: boolean
}

export const addAction = (id: string, body: ActionChange) => api<MeetingFull>(`/api/meetings/${id}/actions`, { body })
export const changeAction = (id: string, actionId: string, body: ActionChange) => api<MeetingFull>(`/api/meetings/${id}/actions/${actionId}`, { method: 'PUT', body })
export const removeAction = (id: string, actionId: string) => api<MeetingFull>(`/api/meetings/${id}/actions/${actionId}`, { method: 'DELETE' })
export const planActions = (id: string) => api<{ id: string }>(`/api/meetings/${id}/plan`, { body: {} })
export const askMeeting = (id: string, question?: string) =>
  api<{ id: string; message: string; attachment: Attachment }>(
    `/api/meetings/${id}/ask`,
    { body: { question } },
  )

/** "12:05", or "1:02:05" past an hour. */
export function clock(seconds: number): string {
  const s = Math.max(0, Math.floor(seconds))
  const h = Math.floor(s / 3600)
  const m = Math.floor((s % 3600) / 60)
  const pad = (n: number) => String(n).padStart(2, '0')
  return h > 0 ? `${h}:${pad(m)}:${pad(s % 60)}` : `${pad(m)}:${pad(s % 60)}`
}
