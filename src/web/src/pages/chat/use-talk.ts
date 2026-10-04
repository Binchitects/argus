import { useCallback, useEffect, useRef, useState } from 'react'
import { toast } from '@/components/ui/toaster'
import { api, ApiError, errorMessage } from '@/lib/api'
import type { ChatEvent } from './types'
import { level, Sentences, SpeechQueue, tuning, VoiceActivity, type Voice } from './voice'

/** Where Talk is: off, or listening, hearing someone, writing down what was said, waiting for the answer, reading it aloud. */
export type TalkState = 'off' | 'starting' | 'listening' | 'hearing' | 'transcribing' | 'thinking' | 'speaking'

/** What the page gives an answer Talk asked for: its events as they come, and its end (however it ended). */
export interface TalkTurn {
  watch: (e: ChatEvent) => void
  ended: () => void
}

/** The browser can listen: a microphone, recording and Web Audio. */
export const canTalk = () =>
  typeof navigator !== 'undefined' && !!navigator.mediaDevices?.getUserMedia && typeof MediaRecorder !== 'undefined' && typeof AudioContext !== 'undefined'

/** Each sentence through the app's text to speech (Persian in a Persian voice), played as an MP3. */
export const browserVoice: Voice = {
  fetch: async (text, signal) => {
    const res = await fetch('/api/chat/speech', {
      method: 'POST',
      signal,
      credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json', 'X-Requested-With': 'fetch' },
      body: JSON.stringify({ text }),
    })
    // Nothing to say in it (only marks, a link): on to the next.
    if (res.status === 400) return null
    if (!res.ok) {
      const body = (await res.json().catch(() => null)) as { error?: string } | null
      throw new ApiError(res.status, String(res.status), body?.error ?? `Reading aloud failed (HTTP ${res.status}).`)
    }
    return res.blob()
  },
  play: (sound, signal) =>
    new Promise<void>((resolve) => {
      if (signal.aborted) return resolve()
      const url = URL.createObjectURL(sound)
      const audio = new Audio(url)
      const done = () => {
        audio.pause()
        URL.revokeObjectURL(url)
        signal.removeEventListener('abort', done)
        resolve()
      }
      audio.onended = done
      audio.onerror = done
      signal.addEventListener('abort', done)
      audio.play().catch(done)
    }),
}

interface Live {
  stream: MediaStream
  context: AudioContext
  analyser: AnalyserNode
  samples: Float32Array<ArrayBuffer>
  timer: ReturnType<typeof setInterval>
  recorder: MediaRecorder | null
  recordingSince: number
  vad: VoiceActivity
  queue: SpeechQueue
}

/**
 * Talk: a voice conversation in the chat. The microphone is listened to all along; what is
 * said up to a pause is written down and asked (`onHeard`, as a spoken question); the answer
 * is read aloud sentence by sentence as it is written; speaking over it stops the reading
 * and the answer (`onInterrupt`).
 */
export function useTalk({ onHeard, onInterrupt, voice = browserVoice }: { onHeard: (text: string, turn: TalkTurn) => void; onInterrupt: () => void; voice?: Voice }) {
  const [state, setState] = useState<TalkState>('off')
  // The latest callbacks: Talk calls them long after the render that made them.
  const handlers = useRef({ onHeard, onInterrupt })
  useEffect(() => {
    handlers.current = { onHeard, onInterrupt }
  })
  const live = useRef<Live | null>(null)
  /** The answer being heard: its number (a newer one or an interruption leaves older ones' words unsaid), its sentences, whether it is over. */
  const turn = useRef<{ n: number; sentences: Sentences; over: boolean } | null>(null)
  const turns = useRef(0)
  /** What was said before a pause that turned out to be mid-sentence: it goes with what comes next. */
  const pending = useRef('')
  const heard = useRef<'idle' | 'hearing' | 'transcribing'>('idle')
  /** Recordings that end with something said (the others are dropped: silence). */
  const kept = useRef(new WeakSet<MediaRecorder>())
  const warned = useRef(false)

  /** The state the parts say: hearing and writing down first, then the answer. */
  const settle = useCallback(() => {
    const t = live.current
    if (!t) return setState('off')
    if (heard.current === 'hearing') return setState('hearing')
    if (heard.current === 'transcribing') return setState('transcribing')
    if (t.queue.playing) return setState('speaking')
    if (turn.current && (!turn.current.over || t.queue.busy)) return setState('thinking')
    turn.current = null
    setState('listening')
  }, [])

  const end = useCallback(() => {
    const t = live.current
    live.current = null
    if (!t) return
    clearInterval(t.timer)
    t.queue.stop()
    if (t.recorder && t.recorder.state !== 'inactive') t.recorder.stop()
    t.stream.getTracks().forEach((track) => track.stop())
    void t.context.close().catch(() => {})
    turn.current = null
    pending.current = ''
    heard.current = 'idle'
    setState('off')
  }, [])

  const finish = (n: number) => {
    const now = turn.current
    if (now?.n !== n || now.over) return
    now.over = true
    for (const s of now.sentences.flush()) live.current?.queue.say(s)
    settle()
  }

  const transcribe = async (sound: Blob) => {
    const form = new FormData()
    const ext = sound.type.includes('mp4') ? 'm4a' : sound.type.includes('ogg') ? 'ogg' : 'webm'
    form.append('file', new File([sound], `talk.${ext}`, { type: sound.type || 'audio/webm' }))
    let text = ''
    try {
      text = (await api<{ text: string }>('/api/chat/transcribe', { body: form })).text.trim()
    } catch (e) {
      toast.error('What you said could not be written down', { description: errorMessage(e) })
    }
    if (!live.current) return
    // Speaking again already: this was half a sentence, and goes with the rest.
    if (live.current.vad.speaking) {
      pending.current = `${pending.current} ${text}`.trim()
      return
    }
    heard.current = 'idle'
    const question = `${pending.current} ${text}`.trim()
    pending.current = ''
    if (!question) return settle()
    const n = ++turns.current
    turn.current = { n, sentences: new Sentences(), over: false }
    settle()
    handlers.current.onHeard(question, {
      watch: (e) => {
        const now = turn.current
        if (now?.n !== n || !live.current) return
        if (e.type === 'content') for (const s of now.sentences.push(e.text)) live.current.queue.say(s)
        if (e.type === 'done' || e.type === 'error' || e.type === 'stopped') finish(n)
      },
      ended: () => finish(n),
    })
  }

  /** A new recording, at once: the start of what is said next must not be lost. */
  const record = () => {
    const t = live.current
    if (!t) return
    const type = MediaRecorder.isTypeSupported?.('audio/webm;codecs=opus') ? 'audio/webm;codecs=opus' : ''
    const recorder = new MediaRecorder(t.stream, type ? { mimeType: type } : undefined)
    const chunks: Blob[] = []
    recorder.ondataavailable = (e) => {
      if (e.data.size > 0) chunks.push(e.data)
    }
    recorder.onstop = () => {
      if (kept.current.has(recorder)) void transcribe(new Blob(chunks, { type: recorder.mimeType || 'audio/webm' }))
    }
    recorder.start()
    t.recorder = recorder
    t.recordingSince = Date.now()
  }

  /** Stops whatever is being said, and the answer it belongs to. */
  const interrupt = () => {
    live.current?.queue.stop()
    if (turn.current && !turn.current.over) handlers.current.onInterrupt()
    turn.current = null
  }

  const tick = () => {
    const t = live.current
    if (!t) return
    t.analyser.getFloatTimeDomainData(t.samples)
    const now = Date.now()
    const event = t.vad.push(level(t.samples), now, t.queue.playing)
    if (event === 'start') {
      interrupt()
      heard.current = 'hearing'
      settle()
    } else if (event === 'end' && t.recorder) {
      // What was said goes to be written down; a new recording starts at once for what comes next.
      const said = t.recorder
      kept.current.add(said)
      heard.current = 'transcribing'
      record()
      said.stop()
      settle()
    } else if (!t.vad.speaking && now - t.recordingSince > tuning.restartMs && t.recorder) {
      // Only silence so far: start again, so the recording that is sent stays small.
      const quiet = t.recorder
      record()
      quiet.stop()
    }
  }

  const start = async () => {
    if (live.current) return
    setState('starting')
    try {
      const stream = await navigator.mediaDevices.getUserMedia({ audio: { echoCancellation: true, noiseSuppression: true, autoGainControl: true } })
      const context = new AudioContext()
      const analyser = context.createAnalyser()
      analyser.fftSize = 1024
      context.createMediaStreamSource(stream).connect(analyser)
      const queue = new SpeechQueue({
        fetch: (text, signal) =>
          voice.fetch(text, signal).catch((e: unknown) => {
            if (!signal.aborted && !warned.current) {
              warned.current = true
              toast.error('The answer cannot be read aloud', { description: errorMessage(e) })
            }
            return null
          }),
        play: voice.play,
      })
      queue.onChange = settle
      warned.current = false
      live.current = {
        stream, context, analyser, samples: new Float32Array(analyser.fftSize), timer: setInterval(tick, tuning.frameMs),
        recorder: null, recordingSince: Date.now(), vad: new VoiceActivity(), queue,
      }
      record()
      setState('listening')
    } catch (e) {
      setState('off')
      toast.error('The microphone is not available', { description: errorMessage(e) })
    }
  }

  // Leaving the chat ends Talk; so does Escape.
  useEffect(() => end, [end])
  useEffect(() => {
    if (state === 'off') return
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') end()
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [state, end])

  return { state, start, end }
}
