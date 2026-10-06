import { useCallback, useEffect, useRef, useState, useSyncExternalStore, type RefObject } from "react";

// Behind the sign-in form: Argus's logo, then its eyes (Argus Panoptes, the
// hundred-eyed watcher) opening across the screen from where the logo was,
// blinking and looking about, then closing back into it; over and over. One turn
// is CYCLE ms. Everything moves by Web Animations of transform and opacity, which
// the browser runs off the main thread: no script runs per frame.

const CYCLE = 12_000;
const OPEN_AT = 3_000; // the logo has gone; the nearest eyes open
const CLOSE_AT = 9_600; // the farthest eyes start closing; the nearest close last
const SPREAD = 1_800; // how long the wave takes from the logo to the farthest eye
const OPENING = 450;
const CLOSING = 400;
const RATIO = 14 / 24; // an eye's height to its width

interface Eye { key: string; x: number; y: number; width: number; open: number; close: number; blink: number; blinkAt: number; look: number; lookAt: number; reverse: boolean }
interface Box { left: number; top: number; right: number; bottom: number }

/** At most this many eyes on a screen this wide: fewer on a phone. */
function cap(width: number) {
  return width < 640 ? 30 : width < 1100 ? 64 : 110;
}

/** A small seeded random number generator: the same screen gets the same eyes. */
function seeded(seed: number) {
  let a = seed >>> 0;
  return () => {
    a = (a + 0x6d2b79f5) >>> 0;
    let t = Math.imul(a ^ (a >>> 15), 1 | a);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4_294_967_296;
  };
}

/** Eyes on a staggered grid, a little out of line, none over `clear`; bigger ones while they would be too many. */
function place(width: number, height: number, origin: { x: number; y: number }, clear: Box[]): Eye[] {
  if (width <= 0 || height <= 0) return [];
  const grid = (size: number) => {
    const px = size * 1.8, py = size * 1.3, margin = size * 0.25;
    const out: { key: string; x: number; y: number; width: number; random: () => number }[] = [];
    for (let row = 0; row < Math.ceil(height / py); row++)
      for (let col = 0; col <= Math.ceil(width / px); col++) {
        const random = seeded(row * 7919 + col * 104_729 + 7);
        const w = size * (0.75 + random() * 0.45), h = w * RATIO;
        const x = (col + (row % 2) * 0.5) * px + (random() - 0.5) * px * 0.35;
        const y = (row + 0.5) * py + (random() - 0.5) * py * 0.3;
        if (x < w / 4 || x > width - w / 4 || y < h / 2 || y > height - h / 4) continue;
        const l = x - w / 2 - margin, r = x + w / 2 + margin, t = y - h / 2 - margin, b = y + h / 2 + margin;
        if (clear.some((c) => l < c.right && c.left < r && t < c.bottom && c.top < b)) continue;
        out.push({ key: `${row}:${col}`, x, y, width: w, random });
      }
    return out;
  };
  const most = cap(width);
  let size = Math.min(56, Math.max(28, width / 22));
  let cells = grid(size);
  for (let round = 0; round < 8 && cells.length > most; round++) {
    size *= Math.sqrt(cells.length / most) * 1.03;
    cells = grid(size);
  }
  cells = cells.slice(0, most);
  const far = Math.max(1, ...cells.map((c) => Math.hypot(c.x - origin.x, c.y - origin.y)));
  return cells.map(({ random, ...c }) => {
    const t = Math.hypot(c.x - origin.x, c.y - origin.y) / far;
    const blink = 3.2 + random() * 3.8, look = 5 + random() * 4;
    return {
      ...c,
      open: Math.round(OPEN_AT + t * SPREAD + (random() - 0.5) * 240),
      close: Math.round(CLOSE_AT + (1 - t) * SPREAD + (random() - 0.5) * 240),
      blink, blinkAt: -random() * blink, look, lookAt: -random() * look, reverse: random() < 0.5,
    };
  });
}

const easeOut = "cubic-bezier(0.16, 1, 0.3, 1)";
const easeIn = "cubic-bezier(0.7, 0, 0.84, 0)";
const at = (ms: number) => ms / CYCLE;

function turnFrames(open: number, close: number): Keyframe[] {
  const shut = { opacity: 0, transform: "scale(0.6, 0.05)" };
  const wide = { opacity: 1, transform: "scale(1, 1)" };
  return [
    { ...shut, offset: 0 },
    { ...shut, offset: at(open), easing: easeOut },
    { ...wide, offset: at(open + OPENING) },
    { ...wide, offset: at(close), easing: easeIn },
    { ...shut, offset: at(close + CLOSING) },
    { ...shut, offset: 1 },
  ];
}
const logoFrames: Keyframe[] = [
  { offset: 0, opacity: 0, transform: "scale(0.9)", easing: easeOut },
  { offset: at(800), opacity: 1, transform: "scale(1)" },
  { offset: at(2_500), opacity: 1, transform: "scale(1)", easing: easeIn },
  { offset: at(3_300), opacity: 0, transform: "scale(0.8)" },
  { offset: 1, opacity: 0, transform: "scale(0.9)" },
];
const blinkFrames: Keyframe[] = [
  { offset: 0, transform: "scaleY(1)" },
  { offset: 0.94, transform: "scaleY(1)" },
  { offset: 0.97, transform: "scaleY(0.08)" },
  { offset: 1, transform: "scaleY(1)" },
];
const lookFrames: Keyframe[] = [
  [0, 0, 0], [0.12, 0, 0], [0.2, -8, 2], [0.34, -8, 2], [0.42, 8, -3], [0.56, 8, -3], [0.64, 3, 3], [0.78, 3, 3], [0.86, 0, 0], [1, 0, 0],
].map(([offset, x, y]) => ({ offset, transform: `translate(${x}%, ${y}%)`, easing: "ease-in-out" }));

const motionOff = "(prefers-reduced-motion: reduce)";
function useStill() {
  return useSyncExternalStore(
    (on) => {
      const mq = window.matchMedia(motionOff);
      mq.addEventListener("change", on);
      return () => mq.removeEventListener("change", on);
    },
    () => window.matchMedia(motionOff).matches,
  );
}

/**
 * The scene, filling the element it is put in, behind the rest and deaf to the
 * pointer. The eyes keep off whatever beside it is marked data-keep-clear (the
 * form). `logo` is the page's own logo, with the class eyes-logo; with reduced
 * motion it stands still and no eye is drawn.
 */
export default function Eyes({ logo }: { logo: RefObject<HTMLElement | null> }) {
  const still = useStill();
  const stage = useRef<HTMLDivElement>(null);
  const [eyes, setEyes] = useState<Eye[]>([]);
  // One clock for the logo and every eye: one laid out later keeps in step.
  const started = useRef<number | null>(null);
  const clock = useCallback(() => (started.current ??= (document.timeline.currentTime as number | null) ?? performance.now()), []);

  useEffect(() => {
    const el = stage.current;
    const page = el?.parentElement;
    if (still || !el || !page) return;
    let frame = 0;
    const layout = () => {
      frame = 0;
      const s = el.getBoundingClientRect();
      const local = (b: DOMRect): Box => ({ left: b.left - s.left, top: b.top - s.top, right: b.right - s.left, bottom: b.bottom - s.top });
      const l = logo.current?.getBoundingClientRect();
      const origin = l?.width ? { x: (l.left + l.right) / 2 - s.left, y: (l.top + l.bottom) / 2 - s.top } : { x: s.width / 2, y: s.height / 3 };
      const clear = [...page.querySelectorAll("[data-keep-clear]")].map((e) => local(e.getBoundingClientRect())).filter((b) => b.right > b.left);
      setEyes(place(s.width, s.height, origin, clear));
    };
    layout();
    const watch = new ResizeObserver(() => {
      frame ||= requestAnimationFrame(layout);
    });
    watch.observe(el);
    for (const e of page.querySelectorAll("[data-keep-clear]")) watch.observe(e);
    return () => {
      watch.disconnect();
      cancelAnimationFrame(frame);
    };
  }, [still, logo]);

  useEffect(() => {
    const el = logo.current;
    if (still || !el?.animate) return;
    const a = el.animate(logoFrames, { duration: CYCLE, iterations: Infinity });
    a.startTime = clock();
    return () => a.cancel();
  }, [still, logo, clock]);

  return (
    <div ref={stage} className="eyes" aria-hidden="true">
      {!still && eyes.map((eye) => <EyeView key={eye.key} eye={eye} clock={clock} />)}
    </div>
  );
}

/** One eye in the logo's line style: the lid's outline, and the iris ring with its pupil, which looks about. */
function EyeView({ eye, clock }: { eye: Eye; clock: () => number }) {
  const ref = useRef<HTMLDivElement>(null);
  const lid = useRef<HTMLDivElement>(null);
  const iris = useRef<HTMLDivElement>(null);
  const { open, close, blink, blinkAt, look, lookAt, reverse } = eye;
  useEffect(() => {
    if (!ref.current?.animate || !lid.current || !iris.current) return;
    const turn = ref.current.animate(turnFrames(open, close), { duration: CYCLE, iterations: Infinity });
    turn.startTime = clock();
    const blinks = lid.current.animate(blinkFrames, { duration: blink * 1000, delay: blinkAt * 1000, iterations: Infinity });
    const looks = iris.current.animate(lookFrames, { duration: look * 1000, delay: lookAt * 1000, iterations: Infinity, direction: reverse ? "reverse" : "normal" });
    return () => {
      turn.cancel();
      blinks.cancel();
      looks.cancel();
    };
  }, [open, close, blink, blinkAt, look, lookAt, reverse, clock]);
  // Each moving part is a box of its own: an <svg> itself is never moved off the main thread.
  return (
    <div ref={ref} className="eye" style={{ left: eye.x - eye.width / 2, top: eye.y - (eye.width * RATIO) / 2, width: eye.width }}>
      <div ref={lid} className="eye-part">
        <svg viewBox="0 0 24 14">
          <path className="eye-lid" d="M2 7C6 1.6 18 1.6 22 7C18 12.4 6 12.4 2 7Z" />
        </svg>
        <div ref={iris} className="eye-part">
          <svg viewBox="0 0 24 14">
            <circle className="eye-iris" cx="12" cy="7" r="2.6" />
            <circle className="eye-pupil" cx="12" cy="7" r="1.1" />
          </svg>
        </div>
      </div>
    </div>
  );
}
