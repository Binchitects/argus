/**
 * The sign-in page's scene, as numbers: where the eyes go and when each opens and
 * closes. One turn (CYCLE ms): the logo appears; as it goes, eyes open from where it
 * was out to the edges; they blink and look about; then they close from the edges
 * back in, and the logo appears again.
 */
export const CYCLE = 12_000
/** The logo has gone by then and the nearest eyes open. */
const OPEN_AT = 3_000
/** The farthest eyes start closing then; the nearest close last, just before the logo is back. */
const CLOSE_AT = 9_600
/** How long the wave takes from the logo to the farthest eye. */
const SPREAD = 1_800
export const OPENING = 450
export const CLOSING = 400

/** An eye's drawing (the logo's own eye): its box in the logo's units. */
export const EYE_VIEW = '276 410 472 242'
export const EYE_RATIO = 242 / 472

export interface Eye {
  key: string
  /** Its centre and width, in px from the stage's top left. */
  x: number
  y: number
  width: number
  /** When it opens and starts closing, in ms of the turn. */
  open: number
  close: number
  /** Its blinks and its looks about: how often and where in that it starts (s). */
  blink: number
  blinkAt: number
  look: number
  lookAt: number
  /** Looks about the other way round. */
  reverse: boolean
}

export interface Box {
  left: number
  top: number
  right: number
  bottom: number
}

/**
 * At most this many eyes on a screenful this wide, counting those the form and the
 * words then hide (on a phone they hide more than half): fewer on a phone.
 */
export function eyeCap(width: number): number {
  return width < 640 ? 72 : width < 1100 ? 110 : 160
}

/** A small seeded random number generator (mulberry32): the same screen gets the same eyes. */
function seeded(seed: number) {
  let a = seed >>> 0
  return () => {
    a = (a + 0x6d2b79f5) >>> 0
    let t = Math.imul(a ^ (a >>> 15), 1 | a)
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t
    return ((t ^ (t >>> 14)) >>> 0) / 4_294_967_296
  }
}

const overlaps = (a: Box, b: Box) => a.left < b.right && b.left < a.right && a.top < b.bottom && b.top < a.bottom

/** A staggered grid of eyes `size` px wide over a stage, a little out of line, each with the room it keeps around it. */
function grid(width: number, height: number, size: number) {
  const pitchX = size * 1.75
  const pitchY = size * 1.2
  const margin = size * 0.25
  const eyes: { key: string; x: number; y: number; width: number; room: Box; random: () => number }[] = []
  for (let row = 0; row < Math.ceil(height / pitchY); row++)
    for (let col = 0; col <= Math.ceil(width / pitchX); col++) {
      const random = seeded(row * 7919 + col * 104_729 + 1)
      const w = size * (0.75 + random() * 0.45)
      const h = w * EYE_RATIO
      const x = (col + (row % 2) * 0.5) * pitchX + (random() - 0.5) * pitchX * 0.35
      const y = (row + 0.5) * pitchY + (random() - 0.5) * pitchY * 0.3
      if (x < w / 4 || x > width - w / 4 || y < h / 2 || y > height - h / 4) continue
      const room = { left: x - w / 2 - margin, top: y - h / 2 - margin, right: x + w / 2 + margin, bottom: y + h / 2 + margin }
      eyes.push({ key: `${row}:${col}`, x, y, width: w, room, random })
    }
  return eyes
}

/**
 * Where the eyes go on a stage `width` × `height`, and when each opens and closes:
 * the screen filled, none over `clear` (the form, the words). Their size and places
 * come from the stage alone, at most `eyeCap(width)` on a screenful of it (`view` px
 * high; bigger eyes on a big screen), and those over `clear` are then left out: when
 * the form grows or shrinks (an error shown, the next step), only the eyes beside it
 * come or go, and every other stays where it is. The farther from `origin` (the logo),
 * the later an eye opens and the sooner it closes.
 */
export function placeEyes(width: number, height: number, origin: { x: number; y: number }, clear: Box[], view = height): Eye[] {
  if (width <= 0 || height <= 0) return []
  const cap = eyeCap(width)
  const seen = Math.min(height, view)
  let size = Math.min(64, Math.max(30, width / 20))
  for (let round = 0, count = grid(width, seen, size).length; round < 8 && count > cap; round++) {
    size *= Math.sqrt(count / cap) * 1.03
    count = grid(width, seen, size).length
  }
  // A page taller than the screen (a small phone) has rows below it too, as many a screenful.
  const cells = grid(width, height, size).slice(0, Math.ceil((cap * height) / seen))
  // The wave reaches the farthest corner of a screenful last, whatever the form or the page does.
  const far = Math.max(1, ...[[0, 0], [width, 0], [0, seen], [width, seen]].map(([x, y]) => Math.hypot(x - origin.x, y - origin.y)))
  return cells
    .filter((c) => !clear.some((b) => overlaps(c.room, b)))
    .map(({ key, x, y, width: w, random }) => {
      const t = Math.min(1, Math.hypot(x - origin.x, y - origin.y) / far)
      const blink = 3.2 + random() * 3.8
      const look = 5 + random() * 4
      return {
        key,
        x,
        y,
        width: w,
        open: Math.round(OPEN_AT + t * SPREAD + (random() - 0.5) * 240),
        close: Math.round(CLOSE_AT + (1 - t) * SPREAD + (random() - 0.5) * 240),
        blink,
        blinkAt: -random() * blink,
        look,
        lookAt: -random() * look,
        reverse: random() < 0.5,
      }
    })
}

/** An eye drawn: one in the layout, or one going (the form grew over it), which fades out before it is dropped. */
export interface Shown extends Eye {
  going?: boolean
}

/** The eyes to draw after a new layout: those in it, and those drawn before and not in it any more, kept to fade out. */
export function nextShown(shown: Shown[], eyes: Eye[]): Shown[] {
  const now = new Set(eyes.map((e) => e.key))
  return [...eyes, ...shown.filter((e) => !now.has(e.key)).map((e) => (e.going ? e : { ...e, going: true }))]
}

/** How long an eye takes to fade in when it comes, or out when it goes, beside a form that changed size. */
export const FADE = 350

const easeOut = 'cubic-bezier(0.16, 1, 0.3, 1)'
const easeIn = 'cubic-bezier(0.7, 0, 0.84, 0)'
const at = (ms: number) => ms / CYCLE

/** An eye's turn: shut (a line), opening like a lid, open, shutting again. */
export function eyeFrames(open: number, close: number): Keyframe[] {
  const shut = { opacity: 0, transform: 'scale(0.6, 0.05)' }
  const wide = { opacity: 1, transform: 'scale(1, 1)' }
  return [
    { ...shut, offset: 0 },
    { ...shut, offset: at(open), easing: easeOut },
    { ...wide, offset: at(open + OPENING) },
    { ...wide, offset: at(close), easing: easeIn },
    { ...shut, offset: at(close + CLOSING) },
    { ...shut, offset: 1 },
  ]
}

/** A blink, at the end of each `blink` seconds. */
export const blinkFrames: Keyframe[] = [
  { offset: 0, transform: 'scaleY(1)' },
  { offset: 0.94, transform: 'scaleY(1)' },
  { offset: 0.97, transform: 'scaleY(0.06)' },
  { offset: 1, transform: 'scaleY(1)' },
]

/** The iris looks left, right, down a little and back, resting at each (in % of the eye). */
export const lookFrames: Keyframe[] = [
  [0, 0, 0],
  [0.12, 0, 0],
  [0.2, -8.5, 1.5],
  [0.34, -8.5, 1.5],
  [0.42, 8, -2],
  [0.56, 8, -2],
  [0.64, 3, 2],
  [0.78, 3, 2],
  [0.86, 0, 0],
  [1, 0, 0],
].map(([offset, x, y]) => ({ offset, transform: `translate(${x}%, ${y}%)`, easing: 'ease-in-out' }))

/** The logo's turn: it appears, stays, and goes as the first eyes open. */
export const logoFrames: Keyframe[] = [
  { offset: 0, opacity: 0, transform: 'scale(0.9)', easing: easeOut },
  { offset: at(800), opacity: 1, transform: 'scale(1)' },
  { offset: at(2_500), opacity: 1, transform: 'scale(1)', easing: easeIn },
  { offset: at(3_300), opacity: 0, transform: 'scale(0.8)' },
  { offset: 1, opacity: 0, transform: 'scale(0.9)' },
]
