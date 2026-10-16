/**
 * Events from a stream, gathered and handed on together: once per frame the page draws, or after `wait` ms when no
 * frame comes (a tab in the background draws none). A streamed answer sends an event per token; drawing the thread
 * for each one kept the page busy in a long session, so the thread takes them in one update per frame instead.
 */
export interface Batch<T> {
  /** One more event: handed on with the others at the next frame. */
  push: (item: T) => void
  /** Hands on what is waiting now (the stream ended, or something must see every event so far). */
  flush: () => void
}

export function batched<T>(take: (items: T[]) => void, wait = 100): Batch<T> {
  let waiting: T[] = []
  let frame: number | null = null
  let timer: ReturnType<typeof setTimeout> | null = null
  const flush = () => {
    if (frame !== null) cancelAnimationFrame(frame)
    if (timer !== null) clearTimeout(timer)
    frame = timer = null
    if (waiting.length === 0) return
    const items = waiting
    waiting = []
    take(items)
  }
  return {
    push: (item) => {
      waiting.push(item)
      if (frame !== null || timer !== null) return
      frame = typeof requestAnimationFrame === 'function' ? requestAnimationFrame(flush) : null
      timer = setTimeout(flush, wait)
    },
    flush,
  }
}
