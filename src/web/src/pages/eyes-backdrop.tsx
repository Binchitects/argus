import { useCallback, useEffect, useRef, useState, type RefObject } from 'react'
import { useMedia } from '@/lib/use-media'
import { blinkFrames, CYCLE, EYE_RATIO, EYE_VIEW, eyeFrames, logoFrames, lookFrames, placeEyes, type Box, type Eye } from './eyes'

/**
 * Behind the sign-in form: the Argus logo, then its eyes (Argus Panoptes, the
 * hundred-eyed watcher) opening across the screen, blinking and looking about, then
 * closing back into the logo; over and over. It fills the element it is put in, sits
 * behind the rest and lets every click through; the eyes keep off whatever beside it
 * is marked `data-keep-clear` (the form, the words). They move by transform and
 * opacity only, on the compositor, with no script per frame. With reduced motion,
 * only the logo, still. The logo is the page's own, given by `logo`, with the class
 * `eyes-logo`.
 */
export function EyesBackdrop({ logo }: { logo: RefObject<HTMLElement | null> }) {
  const still = useMedia('(prefers-reduced-motion: reduce)')
  const stage = useRef<HTMLDivElement>(null)
  const [eyes, setEyes] = useState<Eye[]>([])
  // One clock for the logo and every eye: one laid out later (the window resized) keeps in step.
  const started = useRef<number | null>(null)
  const clock = useCallback(() => (started.current ??= (document.timeline?.currentTime as number | null) ?? performance.now()), [])

  // Lays the eyes out, and again when the page or what they keep off changes size.
  useEffect(() => {
    const el = stage.current
    const page = el?.parentElement
    if (still || !el || !page) return
    let frame = 0
    const layout = () => {
      frame = 0
      const s = el.getBoundingClientRect()
      // Not laid out (a test's page): the window's size.
      const width = s.width || window.innerWidth
      const height = s.height || window.innerHeight
      const local = (b: DOMRect): Box => ({ left: b.left - s.left, top: b.top - s.top, right: b.right - s.left, bottom: b.bottom - s.top })
      const l = logo.current?.getBoundingClientRect()
      const origin = l?.width ? { x: (l.left + l.right) / 2 - s.left, y: (l.top + l.bottom) / 2 - s.top } : { x: width / 2, y: height / 3 }
      const clear = [...page.querySelectorAll('[data-keep-clear]')].map((e) => local(e.getBoundingClientRect())).filter((b) => b.right > b.left)
      setEyes(placeEyes(width, height, origin, clear))
    }
    const later = () => {
      frame ||= requestAnimationFrame(layout)
    }
    layout()
    const watch = new ResizeObserver(later)
    watch.observe(el)
    for (const e of page.querySelectorAll('[data-keep-clear]')) watch.observe(e)
    return () => {
      watch.disconnect()
      cancelAnimationFrame(frame)
    }
  }, [still, logo])

  // The logo (class eyes-logo) is hidden until this shows it: no flash of it before its turn starts.
  useEffect(() => {
    const el = logo.current
    if (still || !el?.animate) return
    const a = el.animate(logoFrames, { duration: CYCLE, iterations: Infinity })
    a.startTime = clock()
    return () => a.cancel()
  }, [still, logo, clock])

  return (
    <div ref={stage} className="eyes-backdrop" aria-hidden="true">
      <svg width="0" height="0" className="absolute">
        <defs>
          <linearGradient id="eyes-white" x1="0" y1="0" x2="0" y2="1">
            <stop offset="0" stopColor="#ffffff" />
            <stop offset="1" stopColor="#d9e6fb" />
          </linearGradient>
          <radialGradient id="eyes-iris" cx="0.45" cy="0.2" r="0.95">
            <stop offset="0" stopColor="#2fd6ff" />
            <stop offset="0.5" stopColor="#1673e8" />
            <stop offset="1" stopColor="#0a3db5" />
          </radialGradient>
        </defs>
      </svg>
      {!still && eyes.map((eye) => <EyeView key={eye.key} eye={eye} clock={clock} />)}
    </div>
  )
}

/** One eye, drawn as the logo's: the white, and over it the iris, which looks about. */
function EyeView({ eye, clock }: { eye: Eye; clock: () => number }) {
  const ref = useRef<HTMLDivElement>(null)
  const lid = useRef<HTMLDivElement>(null)
  const iris = useRef<HTMLDivElement>(null)
  const { open, close, blink, blinkAt, look, lookAt, reverse } = eye
  useEffect(() => {
    const el = ref.current
    if (!el?.animate || !lid.current || !iris.current) return
    const turn = el.animate(eyeFrames(open, close), { duration: CYCLE, iterations: Infinity })
    turn.startTime = clock()
    // Script-made like its turn, not CSS animations: Chrome runs these on the compositor.
    const blinks = lid.current.animate(blinkFrames, { duration: blink * 1000, delay: blinkAt * 1000, iterations: Infinity })
    const looks = iris.current.animate(lookFrames, { duration: look * 1000, delay: lookAt * 1000, iterations: Infinity, direction: reverse ? 'reverse' : 'normal' })
    return () => {
      turn.cancel()
      blinks.cancel()
      looks.cancel()
    }
  }, [open, close, blink, blinkAt, look, lookAt, reverse, clock])
  return (
    // Each moving part is a box of its own: an <svg> itself is never moved on the compositor.
    <div ref={ref} className="watch-eye" style={{ left: eye.x - eye.width / 2, top: eye.y - (eye.width * EYE_RATIO) / 2, width: eye.width }}>
      <div ref={lid} className="watch-eye-lid">
        <svg viewBox={EYE_VIEW}>
          <path d="M286 540 C372 470 436 420 512 420 C588 420 652 470 738 540 C668 540 612 642 512 642 C412 642 356 540 286 540 Z" fill="url(#eyes-white)" />
        </svg>
        <div ref={iris} className="watch-eye-iris">
          <svg viewBox={EYE_VIEW}>
            <circle cx="512" cy="528" r="96" fill="url(#eyes-iris)" />
            <circle cx="512" cy="530" r="42" fill="#06102a" />
            <circle cx="545" cy="494" r="18" fill="#ffffff" />
          </svg>
        </div>
      </div>
    </div>
  )
}
