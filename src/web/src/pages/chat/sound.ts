import type { Attachment } from './types'

export const isMedia = (a: Attachment) => a.kind === 'audio' || a.kind === 'video'

/** A voice message: what the recording is called, so the answer to it is read aloud. */
export const voicePrefix = 'Voice message'

let playing: { audio: HTMLAudioElement; stop: () => void } | null = null

/** Reads a text aloud through the gateway's text to speech, in the person's voice for its language. One at a time: a new one stops the last. */
export function speak(text: string, onEnd?: () => void): Promise<() => void> {
  return play('/api/chat/speech', { text }, onEnd)
}

/** Plays what the app's text to speech makes of a request (a text read aloud, a voice tried). One at a time: a new one stops the last. */
export async function play(path: string, body: unknown, onEnd?: () => void): Promise<() => void> {
  playing?.stop()
  const res = await fetch(path, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-Requested-With': 'fetch' },
    body: JSON.stringify(body),
  })
  if (!res.ok) {
    const problem = await res.json().catch(() => null)
    throw new Error(problem?.error ?? `Read aloud failed (HTTP ${res.status}).`)
  }
  const url = URL.createObjectURL(await res.blob())
  const audio = new Audio(url)
  const stop = () => {
    audio.pause()
    URL.revokeObjectURL(url)
    if (playing?.audio === audio) playing = null
    onEnd?.()
  }
  audio.onended = stop
  playing = { audio, stop }
  await audio.play()
  return stop
}
