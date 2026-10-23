import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Circle, FileAudio, Loader2, Mic, Square, Upload, Users } from 'lucide-react'
import { useEffect, useRef, useState } from 'react'
import { Link, useNavigate, useSearchParams } from 'react-router'
import { PageHeader } from '@/components/app/page-header'
import { PageSkeleton, QueryError } from '@/components/app/query-state'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { EmptyState } from '@/components/ui/empty-state'
import { Input } from '@/components/ui/input'
import { toast } from '@/components/ui/toaster'
import { errorMessage } from '@/lib/api'
import { clock, meetingsQuery, uploadMeeting, type MeetingBrief } from './api'

const when = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' })

/** A meeting's state in a word: its step while it is made, failed, or how many of its action items are open. */
export function MeetingState({ m }: { m: MeetingBrief }) {
  if (m.status === 'processing')
    return (
      <Badge variant="secondary">
        <Loader2 className="animate-spin" aria-hidden="true" />
        {m.step ?? 'Waiting'}…
      </Badge>
    )
  if (m.status === 'failed') return <Badge variant="destructive">Failed</Badge>
  return <Badge variant={m.open > 0 ? 'warning' : 'success'}>{m.actions === 0 ? 'No action items' : `${m.open} of ${m.actions} action items open`}</Badge>
}

/**
 * The browser's recorder for a meeting: the microphone (the meeting's room, or a call played aloud) recorded as Opus,
 * with the time and how loud it is now; stopped, it is sent to be made a meeting.
 */
function Recorder({ onRecorded, busy, now: startNow }: { onRecorded: (file: File) => void; busy: boolean; now?: boolean }) {
  const [recording, setRecording] = useState<{ recorder: MediaRecorder; since: number; stream: MediaStream } | null>(null)
  const [now, setNow] = useState(() => Date.now())
  const [level, setLevel] = useState(0)
  const chunks = useRef<Blob[]>([])
  useEffect(() => {
    if (!recording) return
    // How loud it is, a few times a second: the person sees the microphone hears the room.
    const audio = new AudioContext()
    const analyser = audio.createAnalyser()
    analyser.fftSize = 512
    audio.createMediaStreamSource(recording.stream).connect(analyser)
    const samples = new Uint8Array(analyser.fftSize)
    const t = setInterval(() => {
      setNow(Date.now())
      analyser.getByteTimeDomainData(samples)
      let peak = 0
      for (const v of samples) peak = Math.max(peak, Math.abs(v - 128))
      setLevel(Math.min(1, peak / 64))
    }, 200)
    return () => {
      clearInterval(t)
      void audio.close()
    }
  }, [recording])
  useEffect(() => () => recording?.stream.getTracks().forEach((t) => t.stop()), [recording])
  const supported = typeof navigator !== 'undefined' && !!navigator.mediaDevices?.getUserMedia && typeof MediaRecorder !== 'undefined'

  const start = async () => {
    try {
      const stream = await navigator.mediaDevices.getUserMedia({ audio: { echoCancellation: false, noiseSuppression: true, autoGainControl: true } })
      const type = MediaRecorder.isTypeSupported('audio/webm;codecs=opus') ? 'audio/webm;codecs=opus' : ''
      // 32 kbit/s: three hours stay under the upload's limit, and speech stays clear.
      const recorder = new MediaRecorder(stream, { ...(type ? { mimeType: type } : {}), audioBitsPerSecond: 32_000 })
      chunks.current = []
      recorder.ondataavailable = (e) => e.data.size > 0 && chunks.current.push(e.data)
      recorder.onstop = () => {
        stream.getTracks().forEach((t) => t.stop())
        const blob = new Blob(chunks.current, { type: recorder.mimeType || 'audio/webm' })
        const ext = blob.type.includes('mp4') ? 'm4a' : blob.type.includes('ogg') ? 'ogg' : 'webm'
        if (blob.size > 0) onRecorded(new File([blob], `Meeting ${new Date().toISOString().slice(0, 16).replace('T', ' ')}.${ext}`, { type: blob.type }))
      }
      // A piece every ten seconds: a long recording is not one buffer at the end.
      recorder.start(10_000)
      setRecording({ recorder, since: Date.now(), stream })
      setNow(Date.now())
    } catch (e) {
      toast.error('The microphone is not available', { description: errorMessage(e) })
    }
  }
  // Opened from the installed app's shortcut (Record a meeting): it starts at once.
  const started = useRef(false)
  useEffect(() => {
    if (!startNow || started.current || !supported) return
    started.current = true
    void start()
  })
  const stop = () => {
    recording?.recorder.stop()
    setRecording(null)
    setLevel(0)
  }
  if (!supported) return <p className="text-sm text-muted-foreground">This browser cannot record: upload a recording instead.</p>
  const seconds = recording ? Math.floor((now - recording.since) / 1000) : 0
  return recording ? (
    <fieldset className="m-0 flex min-w-0 flex-wrap items-center gap-3 border-0 p-0" aria-label="Recording">
      <span className="inline-flex items-center gap-2 text-sm font-medium text-destructive-ink">
        <Circle className="size-3 animate-pulse fill-current" aria-hidden="true" /> Recording
      </span>
      <span className="font-mono text-sm tabular-nums" aria-label="Recorded for">
        {clock(seconds)}
      </span>
      {/* oxlint-disable-next-line jsx-a11y/prefer-tag-over-role -- a styled bar; <meter> cannot be styled consistently */}
      <span className="h-2 w-32 overflow-hidden rounded-full bg-muted" role="meter" aria-label="Loudness" aria-valuemin={0} aria-valuemax={100} aria-valuenow={Math.round(level * 100)}>
        <span className="block h-full rounded-full bg-primary transition-[width] duration-150" style={{ width: `${Math.round(level * 100)}%` }} />
      </span>
      <Button variant="destructive" onClick={stop}>
        <Square className="fill-current" /> Stop and make the minutes
      </Button>
    </fieldset>
  ) : (
    <Button onClick={() => void start()} loading={busy}>
      <Mic /> Record a meeting
    </Button>
  )
}

/** Meetings: recorded here or uploaded, made into who said what and the minutes; each one's own. */
export function MeetingsPage() {
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const list = useQuery(meetingsQuery)
  const [params] = useSearchParams()
  const [title, setTitle] = useState('')
  const picker = useRef<HTMLInputElement>(null)
  const send = useMutation({
    mutationFn: (file: File) => uploadMeeting(file, title),
    onSuccess: (m) => {
      setTitle('')
      void queryClient.invalidateQueries({ queryKey: meetingsQuery.queryKey })
      navigate(`/meetings/${m.id}`)
    },
    onError: (e) => toast.error('The recording was not sent', { description: errorMessage(e) }),
  })
  if (list.isPending) return <PageSkeleton />
  if (list.error) return <QueryError error={list.error} retry={() => list.refetch()} />
  return (
    <>
      <PageHeader
        title="Meetings"
        description="Record a meeting here, or upload one: who said what, the minutes with the decisions and the action items, a reminder for each on its day, and a chat to ask about it."
      />
      <Card className="mb-8">
        <CardHeader>
          <CardTitle className="text-base">A new meeting</CardTitle>
          <CardDescription>Recorded in this browser (the microphone hears the room), or a recording of yours: a sound or a video, up to three hours.</CardDescription>
        </CardHeader>
        <CardContent className="grid gap-4">
          <Input value={title} onChange={(e) => setTitle(e.target.value)} placeholder="Its title (optional: the minutes name it)" aria-label="Title" className="max-w-md" maxLength={300} />
          <div className="flex flex-wrap items-center gap-3">
            <Recorder onRecorded={(file) => send.mutate(file)} busy={send.isPending} now={params.get('record') === '1'} />
            <input
              ref={picker}
              type="file"
              hidden
              accept="audio/*,video/*"
              aria-label="Recording to upload"
              onChange={(e) => {
                const file = e.target.files?.[0]
                e.target.value = ''
                if (file) send.mutate(file)
              }}
            />
            <Button variant="outline" onClick={() => picker.current?.click()} disabled={send.isPending}>
              <Upload /> Upload a recording
            </Button>
          </div>
        </CardContent>
      </Card>
      {list.data.length === 0 ? (
        <EmptyState icon={Users} title="No meetings yet">
          Record one or upload one: the transcript tells who said what, and the minutes what was decided and who does what next.
        </EmptyState>
      ) : (
        <ul className="grid gap-3 md:grid-cols-2 xl:grid-cols-3" aria-label="Your meetings">
          {list.data.map((m) => (
            <li key={m.id}>
              <Link
                to={`/meetings/${m.id}`}
                className="grid h-full gap-1.5 rounded-xl border bg-card p-4 text-sm shadow-xs outline-none transition-colors hover:border-primary/40 focus-visible:ring-[3px] focus-visible:ring-ring"
              >
                <span className="flex items-center gap-2">
                  <FileAudio className="size-4 shrink-0 text-primary-ink" aria-hidden="true" />
                  <span className="min-w-0 truncate font-semibold">{m.title || 'A meeting'}</span>
                </span>
                <span className="text-xs text-muted-foreground">
                  {when.format(new Date(m.createdAt))}
                  {m.seconds ? ` · ${clock(m.seconds)}` : ''}
                </span>
                <span>
                  <MeetingState m={m} />
                </span>
              </Link>
            </li>
          ))}
        </ul>
      )}
    </>
  )
}
