import { useEffect, useRef, type KeyboardEvent, type PointerEvent, type RefObject } from 'react'
import { cn } from '@/lib/utils'

/** Where a drag started: the pointer, and the panel's size then. */
interface Drag {
  at: number
  size: number
}

/** A drag over, however it ended (let go, cancelled, the capture lost, the splitter gone): the page selects text again. */
function endDrag(drag: RefObject<Drag | null>) {
  if (!drag.current) return
  drag.current = null
  document.body.style.cursor = ''
  document.body.style.userSelect = ''
}

/**
 * The line between two panels, dragged to resize one of them (or moved with
 * the arrow keys when focused). `value` is the panel's size in pixels; `grow`
 * says which way the pointer makes it larger: +1 right or down, -1 left or up.
 */
export function Splitter({
  label,
  orientation,
  value,
  min,
  max,
  grow,
  onChange,
}: {
  label: string
  /** vertical: a line between side-by-side panels (it moves left and right). */
  orientation: 'vertical' | 'horizontal'
  value: number
  min: number
  max: number
  grow: 1 | -1
  onChange: (size: number) => void
}) {
  const start = useRef<Drag | null>(null)
  const end = () => endDrag(start)
  // Gone mid-drag (Ctrl+` hides the terminal panel and its edge with it): no pointerup reaches it.
  useEffect(() => {
    const drag = start
    return () => endDrag(drag)
  }, [])
  const clamp = (n: number) => Math.round(Math.min(max, Math.max(min, n)))
  const coordinate = (e: PointerEvent) => (orientation === 'vertical' ? e.clientX : e.clientY)
  const onKey = (e: KeyboardEvent) => {
    const step = e.shiftKey ? 48 : 16
    const back = orientation === 'vertical' ? 'ArrowLeft' : 'ArrowUp'
    const forth = orientation === 'vertical' ? 'ArrowRight' : 'ArrowDown'
    if (e.key === back || e.key === forth) {
      e.preventDefault()
      onChange(clamp(value + (e.key === forth ? step : -step) * grow))
    } else if (e.key === 'Home' || e.key === 'End') {
      e.preventDefault()
      onChange(e.key === 'Home' ? min : max)
    }
  }
  return (
    // oxlint-disable-next-line jsx-a11y/prefer-tag-over-role -- a window splitter (a focusable separator that moves) is not an <hr>
    <div role="separator"
      aria-label={label}
      aria-orientation={orientation}
      aria-valuenow={value}
      aria-valuemin={min}
      aria-valuemax={max}
      tabIndex={0}
      onKeyDown={onKey}
      onPointerDown={(e) => {
        if (e.button !== 0) return
        e.preventDefault()
        e.currentTarget.setPointerCapture(e.pointerId)
        start.current = { at: coordinate(e), size: value }
        document.body.style.cursor = orientation === 'vertical' ? 'col-resize' : 'row-resize'
        document.body.style.userSelect = 'none'
      }}
      onPointerMove={(e) => {
        if (!start.current) return
        onChange(clamp(start.current.size + (coordinate(e) - start.current.at) * grow))
      }}
      onPointerUp={(e) => {
        if (e.currentTarget.hasPointerCapture(e.pointerId)) e.currentTarget.releasePointerCapture(e.pointerId)
        end()
      }}
      onPointerCancel={end}
      onLostPointerCapture={end}
      className={cn(
        'group relative z-10 shrink-0 touch-none bg-border outline-none',
        orientation === 'vertical' ? 'w-px cursor-col-resize' : 'h-px cursor-row-resize',
        // A wider area to grab than the line shows; it lights up while dragged or focused.
        orientation === 'vertical' ? 'before:absolute before:inset-y-0 before:-left-1 before:w-[9px]' : 'before:absolute before:inset-x-0 before:-top-1 before:h-[9px]',
        'after:absolute after:bg-primary after:opacity-0 after:transition-opacity hover:after:opacity-60 focus-visible:after:opacity-100 active:after:opacity-100',
        orientation === 'vertical' ? 'after:inset-y-0 after:-left-px after:w-[3px]' : 'after:inset-x-0 after:-top-px after:h-[3px]',
      )}
    />
  )
}
