import { useCallback, useEffect, useRef, useState } from 'react'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { level, tuning, VoiceActivity } from './voice'

/** This page's own id in a room's events and its call: the others' pages reach it by it. */
export const pageId = typeof crypto !== 'undefined' && 'randomUUID' in crypto ? crypto.randomUUID() : `${Date.now().toString(16)}-0000-4000-8000-${Math.random().toString(16).slice(2, 14).padEnd(12, '0')}`

export interface CallSeat {
  page: string
  person: string
  muted: boolean
}

/** What someone said in the call, as heard; with this reader's translation once it has come. */
export interface Caption {
  id: string
  page: string
  by: string
  text: string
  at: string
  /** By language: its translation (null: it was in that language already). */
  translations?: Record<string, string | null>
}

export type CallEvent =
  | { type: 'call'; seats: CallSeat[] }
  | { type: 'signal'; from: string; by: string; signal: Signal }
  | { type: 'caption'; caption: Caption }

type Signal = { description: RTCSessionDescriptionInit } | { candidate: RTCIceCandidateInit }

/** Captions shown: the last few. */
const MostCaptions = 50
/** Speech longer than this is cut and sent while it goes on: captions come as people speak, not only at their pause. */
const LongestUtterance = 12_000

export const canCall = () =>
  typeof navigator !== 'undefined' && !!navigator.mediaDevices?.getUserMedia && typeof RTCPeerConnection !== 'undefined' && typeof MediaRecorder !== 'undefined'

interface Live {
  stream: MediaStream
  context: AudioContext
  analyser: AnalyserNode
  samples: Float32Array<ArrayBuffer>
  timer: ReturnType<typeof setInterval>
  vad: VoiceActivity
  recorder: MediaRecorder | null
  recordingSince: number
  speakingSince: number | null
  ice: RTCIceServer[]
  peers: Map<string, { pc: RTCPeerConnection; audio: HTMLAudioElement }>
}

/**
 * A room's call on this page: the others heard (each page to every other, WebRTC; the server only passes the signals),
 * this person's microphone to them, and what everyone says as live captions, in the reader's language when they read the
 * room in one. The room's events bring the call's own (who is in it, the signals, the captions) to `on`.
 */
export function useRoomCall(roomId: string | undefined, me: string, readIn: string) {
  const [seats, setSeats] = useState<CallSeat[]>([])
  const [joined, setJoined] = useState(false)
  const [joining, setJoining] = useState(false)
  const [muted, setMuted] = useState(false)
  const [speaking, setSpeaking] = useState(false)
  const [captions, setCaptions] = useState<Caption[]>([])
  const live = useRef<Live | null>(null)
  // The latest, for what runs later (the microphone's ticks, the events).
  const readInNow = useRef(readIn)
  const mutedNow = useRef(muted)
  const captionsNow = useRef(captions)
  useEffect(() => {
    readInNow.current = readIn
    mutedNow.current = muted
    captionsNow.current = captions
  })

  const signal = useCallback(
    (to: string, s: Signal) => {
      if (!roomId) return
      void api(`/api/rooms/${roomId}/call/signal`, { body: { page: pageId, to, signal: s } }).catch(() => undefined)
    },
    [roomId],
  )

  const translate = useCallback(
    (c: Caption, language = readInNow.current) => {
      if (!roomId || !language || c.by === me || c.translations?.[language] !== undefined) return
      void api<{ text: string | null; error: string | null }>(`/api/rooms/${roomId}/call/captions/${c.id}/translation`, { body: { language } })
        .then((t) => !t.error && setCaptions((all) => all.map((x) => (x.id === c.id ? { ...x, translations: { ...x.translations, [language]: t.text } } : x))))
        .catch(() => undefined)
    },
    [roomId, me],
  )

  /** A connection to another page in the call: its sound played here, this microphone sent there. */
  const peer = useCallback(
    (page: string) => {
      const l = live.current!
      const known = l.peers.get(page)
      if (known) return known.pc
      const pc = new RTCPeerConnection({ iceServers: l.ice })
      const audio = new Audio()
      audio.autoplay = true
      for (const track of l.stream.getAudioTracks()) pc.addTrack(track, l.stream)
      pc.ontrack = (e) => {
        audio.srcObject = e.streams[0] ?? new MediaStream([e.track])
        void audio.play().catch(() => undefined)
      }
      pc.onicecandidate = (e) => e.candidate && signal(page, { candidate: e.candidate.toJSON() })
      l.peers.set(page, { pc, audio })
      return pc
    },
    [signal],
  )

  const drop = (page: string) => {
    const p = live.current?.peers.get(page)
    if (!p) return
    p.pc.close()
    p.audio.srcObject = null
    live.current!.peers.delete(page)
  }

  /** This page calls a page already in the call (the one who joins calls; the others answer). */
  const call = useCallback(
    async (page: string) => {
      const pc = peer(page)
      await pc.setLocalDescription(await pc.createOffer())
      signal(page, { description: pc.localDescription!.toJSON() })
    },
    [peer, signal],
  )

  const sendCaption = useCallback(
    (blob: Blob) => {
      if (!roomId || blob.size < 2000) return
      const form = new FormData()
      form.append('file', new File([blob], 'speech.webm', { type: blob.type || 'audio/webm' }))
      form.append('page', pageId)
      void api(`/api/rooms/${roomId}/call/captions`, { body: form }).catch((e: unknown) => console.warn('caption', errorMessage(e)))
    },
    [roomId],
  )

  // Recordings with speech in them, sent when they stop (the others are silence, started again).
  const kept = useRef(new WeakSet<MediaRecorder>())
  const record = useCallback(() => {
    const l = live.current
    if (!l) return
    const recorder = new MediaRecorder(l.stream, MediaRecorder.isTypeSupported('audio/webm;codecs=opus') ? { mimeType: 'audio/webm;codecs=opus' } : {})
    const chunks: Blob[] = []
    recorder.ondataavailable = (e) => e.data.size > 0 && chunks.push(e.data)
    recorder.onstop = () => kept.current.has(recorder) && sendCaption(new Blob(chunks, { type: recorder.mimeType || 'audio/webm' }))
    recorder.start()
    l.recorder = recorder
    l.recordingSince = Date.now()
  }, [sendCaption])

  const tick = useCallback(() => {
    const l = live.current
    if (!l?.recorder) return
    const now = Date.now()
    if (!mutedNow.current) {
      l.analyser.getFloatTimeDomainData(l.samples)
      const event = l.vad.push(level(l.samples), now)
      if (event === 'start') {
        l.speakingSince = now
        setSpeaking(true)
      }
      const tooLong = l.vad.speaking && l.speakingSince !== null && now - l.speakingSince > LongestUtterance
      if (event === 'end' || tooLong) {
        // What was said goes to be written down; a new recording starts at once for what comes next.
        const said = l.recorder
        kept.current.add(said)
        record()
        said.stop()
        l.speakingSince = tooLong ? now : null
        if (event === 'end') setSpeaking(false)
        return
      }
    }
    if (!l.vad.speaking && now - l.recordingSince > tuning.restartMs) {
      // Only silence (or muted) so far: start again, so the recording that is sent stays small.
      const quiet = l.recorder
      record()
      quiet.stop()
    }
  }, [record])

  const leave = useCallback(() => {
    const l = live.current
    if (!l) return
    live.current = null
    clearInterval(l.timer)
    l.recorder?.stop()
    for (const page of [...l.peers.keys()]) {
      const p = l.peers.get(page)!
      p.pc.close()
      p.audio.srcObject = null
    }
    l.stream.getTracks().forEach((t) => t.stop())
    void l.context.close()
    setJoined(false)
    setSpeaking(false)
    if (roomId) void api(`/api/rooms/${roomId}/call?page=${pageId}`, { method: 'DELETE' }).catch(() => undefined)
  }, [roomId])

  const join = useCallback(async () => {
    if (!roomId || live.current) return
    setJoining(true)
    try {
      const stream = await navigator.mediaDevices.getUserMedia({ audio: { echoCancellation: true, noiseSuppression: true, autoGainControl: true } })
      const context = new AudioContext()
      const analyser = context.createAnalyser()
      analyser.fftSize = 1024
      context.createMediaStreamSource(stream).connect(analyser)
      let answer: { seats: CallSeat[]; iceServers: RTCIceServer[]; captions: Caption[] }
      try {
        answer = await api(`/api/rooms/${roomId}/call`, { body: { page: pageId, language: readInNow.current || null, muted: false } })
      } catch (e) {
        stream.getTracks().forEach((t) => t.stop())
        void context.close()
        throw e
      }
      live.current = {
        stream, context, analyser, samples: new Float32Array(analyser.fftSize), timer: setInterval(tick, tuning.frameMs), vad: new VoiceActivity(),
        recorder: null, recordingSince: Date.now(), speakingSince: null, ice: answer.iceServers, peers: new Map(),
      }
      record()
      setSeats(answer.seats)
      setCaptions(answer.captions.slice(-MostCaptions))
      answer.captions.forEach((c) => translate(c))
      setMuted(false)
      setJoined(true)
      for (const s of answer.seats) if (s.page !== pageId) void call(s.page).catch(() => undefined)
    } catch (e) {
      toast.error('Could not join the call', { description: errorMessage(e) })
    } finally {
      setJoining(false)
    }
  }, [roomId, tick, record, translate, call])

  const mute = useCallback(
    (on: boolean) => {
      const l = live.current
      if (!l || !roomId) return
      l.stream.getAudioTracks().forEach((t) => (t.enabled = !on))
      // What was being said is not sent; unmuted, the room's noise is learnt again.
      l.vad = new VoiceActivity()
      l.speakingSince = null
      setSpeaking(false)
      setMuted(on)
      void api(`/api/rooms/${roomId}/call`, { body: { page: pageId, language: readInNow.current || null, muted: on } }).catch(() => undefined)
    },
    [roomId],
  )

  /** The call's events, from the room's. */
  const on = useCallback(
    (e: CallEvent) => {
      if (e.type === 'call') {
        setSeats(e.seats)
        // Those who left: their connections close.
        for (const page of [...(live.current?.peers.keys() ?? [])]) if (!e.seats.some((s) => s.page === page)) drop(page)
        if (live.current && !e.seats.some((s) => s.page === pageId)) {
          // Taken out of the call (the page's events were gone too long, or removed from the room): it leaves too.
          leave()
        }
      } else if (e.type === 'caption') {
        setCaptions((all) => [...all.filter((c) => c.id !== e.caption.id), e.caption].slice(-MostCaptions))
        translate(e.caption)
      } else if (e.type === 'signal' && live.current) {
        const s = e.signal
        void (async () => {
          const pc = peer(e.from)
          if ('description' in s) {
            await pc.setRemoteDescription(s.description)
            if (s.description.type === 'offer') {
              await pc.setLocalDescription(await pc.createAnswer())
              signal(e.from, { description: pc.localDescription!.toJSON() })
            }
          } else if ('candidate' in s) {
            await pc.addIceCandidate(s.candidate).catch(() => undefined)
          }
        })().catch(() => undefined)
      }
    },
    [peer, signal, translate, leave],
  )

  // Reading in another language: what was said is asked in it too.
  useEffect(() => {
    if (readIn) captionsNow.current.forEach((c) => translate(c, readIn))
    if (live.current && roomId) void api(`/api/rooms/${roomId}/call`, { body: { page: pageId, language: readIn || null, muted: mutedNow.current } }).catch(() => undefined)
  }, [readIn, roomId, translate])

  // Leaving the room's page leaves its call.
  useEffect(() => leave, [leave])

  return { seats, joined, joining, muted, speaking, captions, join, leave, mute, on }
}
