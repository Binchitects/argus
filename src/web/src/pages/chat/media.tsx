import { Mic, Square, Volume2, Loader2 } from 'lucide-react'
import { useEffect, useRef, useState } from 'react'
import { toast } from '@/components/ui/toaster'
import { Button } from '@/components/ui/button'
import { Tooltip } from '@/components/ui/tooltip'
import { errorMessage } from '@/lib/api'
import { attachmentUrl } from './api'
import { speak, voicePrefix } from './sound'
import type { Attachment } from './types'

/** A sound or a video, played in place: sound with a player, video with its picture. */
export function MediaPlayer({ a, className }: { a: Attachment; className?: string }) {
  if (a.kind === 'video') {
    return (
      // oxlint-disable-next-line jsx-a11y/media-has-caption -- people's own clips and generated ones have no captions to give
      <video controls preload="metadata" src={attachmentUrl(a.id)} aria-label={a.fileName} className={className ?? 'max-h-72 max-w-full rounded-lg border bg-black'} />
    )
  }
  return (
    <figure className={className ?? 'grid gap-1 rounded-lg border bg-card px-3 py-2'}>
      <figcaption className="truncate text-xs text-muted-foreground">{a.fileName}</figcaption>
      {/* oxlint-disable-next-line jsx-a11y/media-has-caption -- a voice message's transcript is what the model reads */}
      <audio controls preload="metadata" src={attachmentUrl(a.id)} aria-label={a.fileName} className="h-9 w-64 max-w-full" />
    </figure>
  )
}

/** Records a voice message from the microphone and hands it over as a file to attach. */
export function VoiceButton({ onRecorded, disabled }: { onRecorded: (file: File) => void; disabled?: boolean }) {
  const [recording, setRecording] = useState<{ recorder: MediaRecorder; since: number } | null>(null)
  const [now, setNow] = useState(() => Date.now())
  const chunks = useRef<Blob[]>([])
  useEffect(() => {
    if (!recording) return
    const t = setInterval(() => setNow(Date.now()), 250)
    return () => clearInterval(t)
  }, [recording])
  useEffect(() => () => recording?.recorder.stream.getTracks().forEach((t) => t.stop()), [recording])
  if (typeof navigator === 'undefined' || !navigator.mediaDevices?.getUserMedia || typeof MediaRecorder === 'undefined') return null

  const start = async () => {
    try {
      const stream = await navigator.mediaDevices.getUserMedia({ audio: true })
      const type = MediaRecorder.isTypeSupported('audio/webm;codecs=opus') ? 'audio/webm;codecs=opus' : ''
      const recorder = new MediaRecorder(stream, type ? { mimeType: type } : undefined)
      chunks.current = []
      recorder.ondataavailable = (e) => e.data.size > 0 && chunks.current.push(e.data)
      recorder.onstop = () => {
        stream.getTracks().forEach((t) => t.stop())
        const blob = new Blob(chunks.current, { type: recorder.mimeType || 'audio/webm' })
        const ext = blob.type.includes('mp4') ? 'm4a' : blob.type.includes('ogg') ? 'ogg' : 'webm'
        const stamp = new Date().toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit', second: '2-digit' }).replace(/:/g, '.')
        if (blob.size > 0) onRecorded(new File([blob], `${voicePrefix} ${stamp}.${ext}`, { type: blob.type }))
      }
      recorder.start()
      setRecording({ recorder, since: Date.now() })
      setNow(Date.now())
    } catch (e) {
      toast.error('The microphone is not available', { description: errorMessage(e) })
    }
  }
  const stop = () => {
    recording?.recorder.stop()
    setRecording(null)
  }
  const seconds = recording ? Math.floor((now - recording.since) / 1000) : 0
  return recording ? (
    <Button type="button" variant="destructive" size="sm" className="h-8 gap-1.5 rounded-full px-2.5" onClick={stop} aria-label="Stop recording">
      <Square className="fill-current" /> <span className="tabular-nums">{Math.floor(seconds / 60)}:{String(seconds % 60).padStart(2, '0')}</span>
    </Button>
  ) : (
    <Tooltip content="Record a voice message: a model that hears gets your voice, the others a transcript. The answer is read aloud.">
      <Button type="button" variant="ghost" size="icon-sm" onClick={start} disabled={disabled} aria-label="Record a voice message">
        <Mic />
      </Button>
    </Tooltip>
  )
}

/** The button under an answer that reads it aloud, and stops it. */
export function ReadAloud({ text }: { text: string }) {
  const [state, setState] = useState<'idle' | 'loading' | 'playing'>('idle')
  const stopRef = useRef<(() => void) | null>(null)
  useEffect(() => () => stopRef.current?.(), [])
  const click = async () => {
    if (state === 'playing') {
      stopRef.current?.()
      return
    }
    setState('loading')
    try {
      stopRef.current = await speak(text, () => setState('idle'))
      setState('playing')
    } catch (e) {
      setState('idle')
      toast.error(errorMessage(e))
    }
  }
  return (
    <Tooltip content={state === 'playing' ? 'Stop reading' : 'Read aloud'}>
      <Button type="button" variant="ghost" size="icon-sm" className="size-7" onClick={click} aria-label={state === 'playing' ? 'Stop reading' : 'Read aloud'} aria-pressed={state === 'playing'}>
        {state === 'loading' ? <Loader2 className="animate-spin" /> : state === 'playing' ? <Square className="fill-current" /> : <Volume2 />}
      </Button>
    </Tooltip>
  )
}
