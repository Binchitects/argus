import type { Attachment } from './types'

export const isMedia = (a: Attachment) => a.kind === 'audio' || a.kind === 'video'

/** A voice message: what the recording is called, so the answer to it is read aloud. */
export const voicePrefix = 'Voice message'

let playing: { audio: HTMLAudioElement; stop: () => void } | null = null

/** Reads a text aloud through the gateway's text to speech. One at a time: a new one stops the last. */
export async function speak(text: string, onEnd?: () => void): Promise<() => void> {
  playing?.stop()
  const res = await fetch('/api/chat/speech', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-Requested-With': 'fetch' },
    body: JSON.stringify({ text }),
  })
  if (!res.ok) {
    const body = await res.json().catch(() => null)
    throw new Error(body?.error ?? `Read aloud failed (HTTP ${res.status}).`)
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
