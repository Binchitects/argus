/** Screenshots sent with feedback: at most this many, each made small enough to send (the server takes 2 MB). */
export const maxScreenshots = 3
const maxBytes = 2 * 1024 * 1024
const maxSide = 2560

/** A screenshot ready to send: its bytes in base64, and an address to show it by. */
export interface Screenshot {
  data: string
  url: string
  bytes: number
}

function toBase64(blob: Blob): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader()
    reader.onload = () => resolve(String(reader.result).replace(/^data:[^,]*,/, ''))
    reader.onerror = () => reject(reader.error ?? new Error('The picture could not be read.'))
    reader.readAsDataURL(blob)
  })
}

function canvasBlob(canvas: HTMLCanvasElement, type: string, quality: number): Promise<Blob | null> {
  return new Promise((resolve) => canvas.toBlob(resolve, type, quality))
}

/** A picture drawn again no larger than 2560 px a side, as WebP (JPEG where the browser makes no WebP), smaller each try. */
async function shrink(source: CanvasImageSource, width: number, height: number): Promise<Blob> {
  const scale = Math.min(1, maxSide / Math.max(width, height))
  const canvas = document.createElement('canvas')
  canvas.width = Math.max(1, Math.round(width * scale))
  canvas.height = Math.max(1, Math.round(height * scale))
  canvas.getContext('2d')?.drawImage(source, 0, 0, canvas.width, canvas.height)
  for (const quality of [0.9, 0.8, 0.65, 0.5]) {
    const webp = await canvasBlob(canvas, 'image/webp', quality)
    const blob = webp?.type === 'image/webp' ? webp : await canvasBlob(canvas, 'image/jpeg', quality)
    if (blob && blob.size <= maxBytes) return blob
  }
  throw new Error('The picture is too large to send, even made smaller.')
}

async function ready(blob: Blob): Promise<Screenshot> {
  return { data: await toBase64(blob), url: URL.createObjectURL(blob), bytes: blob.size }
}

/** A picture file (picked or pasted): as it is when it is small enough, else drawn again smaller. */
export async function screenshotOfFile(file: Blob): Promise<Screenshot> {
  if (!/^image\/(png|jpeg|gif|webp)$/.test(file.type)) throw new Error('A screenshot is a PNG, JPEG, GIF or WebP picture.')
  if (file.size <= maxBytes) return ready(file)
  const bitmap = await createImageBitmap(file)
  try {
    return await ready(await shrink(bitmap, bitmap.width, bitmap.height))
  } finally {
    bitmap.close()
  }
}

/** Whether this browser can take a picture of the screen (a window, a tab) itself. */
export const canCapture = () => typeof navigator !== 'undefined' && typeof navigator.mediaDevices?.getDisplayMedia === 'function'

/**
 * A picture of what the person chooses to share (this tab first, where the browser offers it): one frame, then the sharing
 * stops. <paramref name="hide"/> is called around the frame, so the dialog asking for it is not in the picture.
 */
export async function captureScreen(hide: (hidden: boolean) => void): Promise<Screenshot> {
  const stream = await navigator.mediaDevices.getDisplayMedia({ video: true, audio: false, preferCurrentTab: true } as DisplayMediaStreamOptions)
  try {
    hide(true)
    const video = document.createElement('video')
    video.muted = true
    video.srcObject = stream
    await video.play()
    // The dialog gone from the page, and a frame of the page without it.
    await new Promise((r) => setTimeout(r, 400))
    return await ready(await shrink(video, video.videoWidth, video.videoHeight))
  } finally {
    for (const track of stream.getTracks()) track.stop()
    hide(false)
  }
}

/** The addresses of a feedback's screenshots that were sent, under its own address (the person's, or Admin → Feedback's). */
export const screenshotUrls = (base: string, count: number) => Array.from({ length: count }, (_, n) => `${base}/images/${n}`)
