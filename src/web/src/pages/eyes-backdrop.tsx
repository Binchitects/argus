import { useCallback, useEffect, useRef, useState, type RefObject } from 'react'
import { useSeeing } from '@/lib/seeing'
import { useMedia } from '@/lib/use-media'
import { blinkFrames, CYCLE, EYE_RATIO, EYE_VIEW, eyeFrames, FADE, logoFrames, lookFrames, nextShown, placeEyes, type Box, type Shown } from './eyes'

/**
 * Behind the sign-in form: the logo, then its eyes (the owl's: the wise one, who sees what
 * others miss) opening across the screen, blinking and looking about, then
 * closing back into the logo; over and over. It fills the element it is put in, sits
 * behind the rest and lets every click through; the eyes keep off whatever beside it
 * is marked `data-keep-clear` (the form, the words). They move by transform and
 * opacity only, on the compositor, with no script per frame. With reduced motion,
 * only the logo, still. The logo is the page's own, given by `logo`, with the class
 * `eyes-logo`.
 */
export function EyesBackdrop({ logo }: { logo: RefObject<HTMLElement | null> }) {
  // Still as the system asks, or as the person chose here (Appearance: Motion).
  const systemStill = useMedia('(prefers-reduced-motion: reduce)')
  const chosen = useSeeing().motion
  const still = systemStill || chosen === 'reduce'
  const stage = useRef<HTMLDivElement>(null)
  const [eyes, setEyes] = useState<Shown[]>([])
  // One clock for the logo and every eye: one laid out later (the window resized) keeps in step.
  const started = useRef<number | null>(null)
  const clock = useCallback(() => (started.current ??= (document.timeline?.currentTime as number | null) ?? performance.now()), [])
  const drop = useCallback((key: string) => setEyes((shown) => shown.filter((e) => !(e.going && e.key === key))), [])

  // Lays the eyes out, and again when the page or what they keep off changes size.
  useEffect(() => {
    const el = stage.current
    const page = el?.parentElement
    if (still || !el || !page) return
    let frame = 0
    // Where the wave starts, measured again only when the stage changes size: the
    // form growing (an error shown) may move the logo a little, not every eye's time.
    let origin: { x: number; y: number; width: number; height: number } | null = null
    const layout = () => {
      frame = 0
      const s = el.getBoundingClientRect()
      // Not laid out (a test's page): the window's size.
      const width = s.width || window.innerWidth
      const height = s.height || window.innerHeight
      const local = (b: DOMRect): Box => ({ left: b.left - s.left, top: b.top - s.top, right: b.right - s.left, bottom: b.bottom - s.top })
      if (!origin || origin.width !== width || origin.height !== height) {
        const l = logo.current?.getBoundingClientRect()
        origin = l?.width ? { x: (l.left + l.right) / 2 - s.left, y: (l.top + l.bottom) / 2 - s.top, width, height } : { x: width / 2, y: height / 3, width, height }
      }
      const from = origin
      const clear = [...page.querySelectorAll('[data-keep-clear]')].map((e) => local(e.getBoundingClientRect())).filter((b) => b.right > b.left)
      // Sized for the screen, not the page: on a small phone the page grows with the form.
      setEyes((shown) => nextShown(shown, placeEyes(width, height, from, clear, window.innerHeight || height)))
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
            <stop offset="1" stopColor="#e3eef0" />
          </linearGradient>
          <radialGradient id="eyes-iris" cx="0.42" cy="0.32" r="0.9">
            <stop offset="0" stopColor="#fde68a" />
            <stop offset="0.55" stopColor="#f59e0b" />
            <stop offset="1" stopColor="#b45309" />
          </radialGradient>
        </defs>
      </svg>
      {!still && eyes.map((eye) => <EyeView key={eye.key} eye={eye} clock={clock} drop={drop} />)}
    </div>
  )
}

/**
 * One owl's eyes, drawn as the logo's: the facial disc, the two whites, the irises (which look about together) under
 * their lids' shade, the brow and the beak. They fade in when they come and out when they go (beside a form that
 * changed size).
 */
function EyeView({ eye, clock, drop }: { eye: Shown; clock: () => number; drop: (key: string) => void }) {
  const ref = useRef<HTMLDivElement>(null)
  const lid = useRef<HTMLDivElement>(null)
  const blinker = useRef<HTMLDivElement>(null)
  const iris = useRef<HTMLDivElement>(null)
  const { key, open, close, blink, blinkAt, look, lookAt, reverse, going } = eye
  // Its turn, on the shared clock.
  useEffect(() => {
    if (!ref.current?.animate) return
    const turn = ref.current.animate(eyeFrames(open, close), { duration: CYCLE, iterations: Infinity })
    turn.startTime = clock()
    return () => turn.cancel()
  }, [open, close, clock])
  // Its blinks and looks about, on its own time. Script-made like its turn, not CSS
  // animations: Chrome runs these on the compositor.
  useEffect(() => {
    if (!blinker.current?.animate || !iris.current) return
    const blinks = blinker.current.animate(blinkFrames, { duration: blink * 1000, delay: blinkAt * 1000, iterations: Infinity })
    const looks = iris.current.animate(lookFrames, { duration: look * 1000, delay: lookAt * 1000, iterations: Infinity, direction: reverse ? 'reverse' : 'normal' })
    return () => {
      blinks.cancel()
      looks.cancel()
    }
  }, [blink, blinkAt, look, lookAt, reverse])
  useEffect(() => {
    const el = lid.current
    if (!el?.animate) {
      if (going) drop(key)
      return
    }
    const fade = el.animate([{ opacity: going ? 1 : 0 }, { opacity: going ? 0 : 1 }], { duration: FADE, easing: 'ease', fill: going ? 'forwards' : 'none' })
    if (going) fade.onfinish = () => drop(key)
    return () => fade.cancel()
  }, [going, key, drop])
  return (
    // Each moving part is a box of its own: an <svg> itself is never moved on the compositor. The face (its disc, brow
    // and beak) stays still; the eyes in it blink and look about, both together, as an owl's do.
    <div ref={ref} className="watch-eye" style={{ left: eye.x - eye.width / 2, top: eye.y - (eye.width * EYE_RATIO) / 2, width: eye.width }}>
      <div ref={lid} className="watch-eye-body">
        <svg viewBox={EYE_VIEW} className="watch-eye-face">
          <path d="M512 468 C468 396 232 388 234 556 C236 702 404 742 512 690 C620 742 788 702 790 556 C792 388 556 396 512 468 Z" />
        </svg>
        <div ref={blinker} className="watch-eye-lid">
          <svg viewBox={EYE_VIEW}>
            <circle cx="392" cy="556" r="124" fill="url(#eyes-white)" />
            <circle cx="632" cy="556" r="124" fill="url(#eyes-white)" />
          </svg>
          <div ref={iris} className="watch-eye-iris">
            <svg viewBox={EYE_VIEW}>
              {[392, 632].map((x) => (
                <g key={x}>
                  <circle cx={x + 6} cy="560" r="72" fill="url(#eyes-iris)" />
                  {/* The iris's fibres, out from the pupil. */}
                  <circle cx={x + 6} cy="560" r="54" fill="none" stroke="#92400e" strokeOpacity="0.35" strokeWidth="30" strokeDasharray="2 6" />
                  <circle cx={x + 8} cy="562" r="34" fill="#0b1720" />
                  <circle cx={x + 30} cy="534" r="14" fill="#ffffff" />
                  <circle cx={x - 12} cy="586" r="6" fill="#ffffff" fillOpacity="0.7" />
                </g>
              ))}
            </svg>
          </div>
          {/* The upper lids' shade over each eye. */}
          <svg viewBox={EYE_VIEW} className="watch-eye-lids">
            <path d="M268 556 A124 124 0 0 1 516 556 A124 96 0 0 0 268 556 Z M508 556 A124 124 0 0 1 756 556 A124 96 0 0 0 508 556 Z" />
          </svg>
        </div>
        <svg viewBox={EYE_VIEW} className="watch-eye-brow">
          <path d="M268 446 Q392 424 512 508 Q632 424 756 446" />
          <path d="M512 640 L476 600 Q512 584 548 600 Z" className="watch-eye-beak" />
        </svg>
      </div>
    </div>
  )
}
