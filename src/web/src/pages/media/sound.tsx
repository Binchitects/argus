import { useMutation, useQueryClient } from '@tanstack/react-query'
import { AudioLines, Mic, Music, Square, Upload, X } from 'lucide-react'
import { useEffect, useId, useRef, useState } from 'react'
import { Segmented } from '@/components/app/segmented'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { Field } from '@/components/ui/field'
import { Textarea } from '@/components/ui/input'
import { toast } from '@/components/ui/toaster'
import { errorMessage } from '@/lib/api'
import { uploadFile } from '../chat/api'
import { mediaJobsQuery, startJob, type MediaJobRequest } from './media-api'

/** The longest recording of a voice sample: ten to thirty seconds is what the voice model needs. */
const MostRecording = 30

function useStart(onStarted: () => void) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (body: MediaJobRequest) => startJob(body),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: mediaJobsQuery.queryKey })
      onStarted()
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
}

/** A sample of a voice: a sound file of yours, or recorded here; uploaded as your file. */
function VoiceSample({ value, onChange }: { value: { id: string; name: string } | null; onChange: (v: { id: string; name: string } | null) => void }) {
  const file = useRef<HTMLInputElement | null>(null)
  const recorder = useRef<MediaRecorder | null>(null)
  const [recording, setRecording] = useState(0)
  const [busy, setBusy] = useState(false)
  const timer = useRef<number | null>(null)

  useEffect(() => () => {
    if (timer.current) window.clearInterval(timer.current)
    recorder.current?.stream.getTracks().forEach((t) => t.stop())
  }, [])

  const upload = async (f: File) => {
    setBusy(true)
    try {
      const a = await uploadFile(f, () => undefined)
      onChange({ id: a.id, name: f.name })
    } catch (e) {
      toast.error(errorMessage(e))
    } finally {
      setBusy(false)
    }
  }

  const record = async () => {
    try {
      const stream = await navigator.mediaDevices.getUserMedia({ audio: true })
      const opus = MediaRecorder.isTypeSupported('audio/webm;codecs=opus') ? 'audio/webm;codecs=opus' : ''
      const r = new MediaRecorder(stream, opus ? { mimeType: opus } : undefined)
      const chunks: Blob[] = []
      r.ondataavailable = (e) => e.data.size > 0 && chunks.push(e.data)
      r.onstop = () => {
        stream.getTracks().forEach((t) => t.stop())
        if (timer.current) window.clearInterval(timer.current)
        setRecording(0)
        const type = r.mimeType || 'audio/webm'
        void upload(new File(chunks, `my-voice.${type.includes('mp4') ? 'm4a' : 'webm'}`, { type }))
      }
      recorder.current = r
      r.start()
      setRecording(1)
      timer.current = window.setInterval(() => {
        setRecording((s) => {
          if (s >= MostRecording && r.state === 'recording') r.stop()
          return s + 1
        })
      }, 1000)
    } catch (e) {
      toast.error(e instanceof DOMException && e.name === 'NotAllowedError' ? 'The browser was not allowed to use the microphone.' : errorMessage(e))
    }
  }

  if (value) {
    return (
      <div className="flex items-center gap-2 rounded-lg border px-3 py-2 text-sm">
        <AudioLines className="size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
        <span className="min-w-0 flex-1 truncate">{value.name}</span>
        <Button type="button" variant="ghost" size="icon-sm" aria-label="Another sample" onClick={() => onChange(null)}>
          <X />
        </Button>
      </div>
    )
  }
  return (
    <div className="flex flex-wrap items-center gap-2">
      <input
        ref={file}
        type="file"
        accept="audio/*,video/*"
        className="hidden"
        aria-label="A sound file of the voice"
        onChange={(e) => {
          const f = e.target.files?.[0]
          e.target.value = ''
          if (f) void upload(f)
        }}
      />
      {typeof MediaRecorder === 'undefined' || !navigator.mediaDevices?.getUserMedia ? null : recording ? (
        <Button type="button" variant="secondary" onClick={() => recorder.current?.stop()}>
          <Square /> Stop ({recording} s)
        </Button>
      ) : (
        <Button type="button" variant="outline" loading={busy} onClick={() => void record()}>
          <Mic /> Record 10 to 30 seconds
        </Button>
      )}
      <Button type="button" variant="outline" disabled={busy || recording > 0} onClick={() => file.current?.click()}>
        <Upload /> A sound file
      </Button>
    </div>
  )
}

/** Words spoken: in the voice model's own voice, or in a voice from a sample you may use. */
export function SpeakForm() {
  const id = useId()
  const [text, setText] = useState('')
  const [own, setOwn] = useState<'model' | 'sample'>('model')
  const [sample, setSample] = useState<{ id: string; name: string } | null>(null)
  const [consent, setConsent] = useState(false)
  const [lively, setLively] = useState('0.5')
  const start = useStart(() => setText(''))
  const ready = text.trim() && (own === 'model' || (sample && consent))
  return (
    <form
      className="grid gap-3"
      onSubmit={(e) => {
        e.preventDefault()
        start.mutate({
          action: 'speak', prompt: text.trim(), exaggeration: Number(lively),
          ...(own === 'sample' && sample ? { voiceId: sample.id, consent } : {}),
        })
      }}
    >
      <Field label="What to say" hint={`${text.length.toLocaleString()} of 3,000 characters`}>
        <Textarea rows={3} maxLength={3000} value={text} onChange={(e) => setText(e.target.value)} placeholder="Welcome to the lunch club! We meet every Thursday at noon in the garden room." />
      </Field>
      <div className="flex flex-wrap items-center gap-2">
        <Segmented label="Voice" value={own} onChange={setOwn} options={[{ value: 'model', label: 'Its own voice' }, { value: 'sample', label: 'A voice from a sample' }]} />
        <Segmented label="How lively" value={lively} onChange={setLively} options={[{ value: '0.35', label: 'Calm' }, { value: '0.5', label: 'Natural' }, { value: '0.9', label: 'Lively' }]} />
      </div>
      {own === 'sample' && (
        <div className="grid gap-2 rounded-lg bg-muted/40 p-3">
          <VoiceSample value={sample} onChange={setSample} />
          <div className="flex items-start gap-2 text-sm">
            <Checkbox id={`${id}-consent`} checked={consent} onCheckedChange={(c) => setConsent(!!c)} className="mt-0.5" />
            <label htmlFor={`${id}-consent`}>This voice is mine, or its owner lets me use it. (What it says is marked as made by a model.)</label>
          </div>
        </div>
      )}
      <Button type="submit" className="justify-self-end" loading={start.isPending} disabled={!ready}>
        <AudioLines /> Speak it
      </Button>
    </form>
  )
}

/** Music from a description, with lyrics or none. */
export function MusicForm() {
  const [prompt, setPrompt] = useState('')
  const [lyrics, setLyrics] = useState('')
  const [seconds, setSeconds] = useState('30')
  const start = useStart(() => undefined)
  return (
    <form
      className="grid gap-3"
      onSubmit={(e) => {
        e.preventDefault()
        start.mutate({ action: 'music', prompt: prompt.trim(), lyrics: lyrics.trim() || undefined, seconds: Number(seconds) })
      }}
    >
      <Field label="Describe the music" hint="Its style, instruments, mood and tempo, as a few words separated by commas.">
        <Textarea rows={2} maxLength={1000} value={prompt} onChange={(e) => setPrompt(e.target.value)} placeholder="upbeat acoustic folk, guitar, hand claps, warm, 110 bpm" />
      </Field>
      <Field label="Lyrics (none: instrumental)">
        <Textarea rows={4} maxLength={6000} value={lyrics} onChange={(e) => setLyrics(e.target.value)} placeholder={'[verse]\nThursday noon, the garden room\n[chorus]\nCome and join the lunch club'} />
      </Field>
      <div className="flex flex-wrap items-center gap-2">
        <Segmented label="How long" value={seconds} onChange={setSeconds} options={[{ value: '15', label: '15 s' }, { value: '30', label: '30 s' }, { value: '60', label: '1 min' }, { value: '120', label: '2 min' }]} />
        <Button type="submit" className="ml-auto" loading={start.isPending} disabled={!prompt.trim()}>
          <Music /> Make the music
        </Button>
      </div>
    </form>
  )
}
