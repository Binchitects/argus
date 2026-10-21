import { useCallback, useEffect, useRef, useState } from 'react'
import { toast } from '@/components/ui/toaster'

/** The longest side of a frame sent: enough to read a screen, small enough to send at once. */
export const FrameSide = 1600

/** Its file's name: the screen, and when it was taken (the model reads it, and the chat shows it). */
export const frameName = (at: Date) => `screen-${at.toTimeString().slice(0, 8).replaceAll(':', '-')}.jpg`

/** Whether this browser can share a screen (desktop browsers; phones cannot). */
export const canShareScreen = () => typeof navigator !== 'undefined' && typeof navigator.mediaDevices?.getDisplayMedia === 'function'

/** A silent video element the shared screen plays in, out of sight: the frames are taken from it. */
function playing(media: MediaStream): HTMLVideoElement {
  const v = document.createElement('video')
  v.muted = true
  v.playsInline = true
  v.srcObject = media
  return v
}

/**
 * Show your screen: a window, a tab or the whole screen, as the browser offers, kept live in the page. Nothing is recorded:
 * each question takes what is on it at that moment (`frame`), as a picture, and sends it with the question.
 */
export function useScreenShare() {
  const [label, setLabel] = useState<string | null>(null)
  const stream = useRef<MediaStream | null>(null)
  const video = useRef<HTMLVideoElement | null>(null)

  const stop = useCallback(() => {
    stream.current?.getTracks().forEach((t) => t.stop())
    stream.current = null
    if (video.current) video.current.srcObject = null
    setLabel(null)
  }, [])

  const start = useCallback(async () => {
    if (!canShareScreen()) {
      toast.error('This browser cannot share a screen.')
      return
    }
    let media: MediaStream
    try {
      media = await navigator.mediaDevices.getDisplayMedia({ video: { frameRate: 5 }, audio: false })
    } catch (e) {
      // Cancelled in the browser's picker: nothing to say.
      if ((e as DOMException)?.name !== 'NotAllowedError' && (e as DOMException)?.name !== 'AbortError') toast.error('The screen could not be shared.')
      return
    }
    stop()
    stream.current = media
    const track = media.getVideoTracks()[0]
    // Stopped from the browser's own bar ("Stop sharing"): so here too.
    track?.addEventListener('ended', stop)
    const v = playing(media)
    video.current = v
    await v.play().catch(() => undefined)
    setLabel(track?.label || 'Your screen')
  }, [stop])

  /** What is on the screen now, as a JPEG file; null when nothing is shared or the frame could not be taken. */
  const frame = useCallback(async (): Promise<File | null> => {
    const v = video.current
    if (!stream.current || !v) return null
    const settings = stream.current.getVideoTracks()[0]?.getSettings?.() ?? {}
    const width = v.videoWidth || settings.width || 1280
    const height = v.videoHeight || settings.height || 720
    const scale = Math.min(1, FrameSide / Math.max(width, height))
    const canvas = document.createElement('canvas')
    canvas.width = Math.round(width * scale)
    canvas.height = Math.round(height * scale)
    const context = canvas.getContext('2d')
    if (!context) return null
    context.drawImage(v, 0, 0, canvas.width, canvas.height)
    const blob = await new Promise<Blob | null>((resolve) => canvas.toBlob(resolve, 'image/jpeg', 0.82))
    return blob ? new File([blob], frameName(new Date()), { type: 'image/jpeg' }) : null
  }, [])

  useEffect(() => stop, [stop])

  return { sharing: label !== null, label, start, stop, frame, video }
}

export type ScreenShare = ReturnType<typeof useScreenShare>
