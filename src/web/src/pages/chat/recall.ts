import { infiniteQueryOptions, useInfiniteQuery, useQueryClient, type InfiniteData } from '@tanstack/react-query'
import { useCallback, useLayoutEffect, useMemo, useRef, useState, type KeyboardEvent, type RefObject } from 'react'
import { api } from '@/lib/api'

/** Messages sent before, beyond this chat's own: a page at a time, as ↑ reaches the end of what came. */
export interface Older {
  /** Newest first. */
  items: string[]
  /** Nothing more to come. */
  done: boolean
  /** Asks for the next page (the first, the first time); all that came so far, newest first. */
  load: () => Promise<string[]>
}

/** What ↑ steps through: this chat's messages (newest first), then the older ones. */
export interface RecallSource {
  here: string[]
  older?: Older
}

/** One list, newest first: empty ones out, and a text sent twice shown once, where it was sent last. */
export function recallList(here: string[], older: string[]): string[] {
  const seen = new Set<string>()
  const out: string[] = []
  for (const t of [...here, ...older]) {
    const text = t.trim()
    if (!text || seen.has(text)) continue
    seen.add(text)
    out.push(text)
  }
  return out
}

/** A message the person sent, as /api/chat/history gives it. */
interface Sent {
  text: string
  at: string
}

const page = 50

/** The person's messages across their chats, newest first, a page at a time (the next one before the last one's time). */
const olderQuery = infiniteQueryOptions({
  queryKey: ['chat', 'history'],
  queryFn: ({ pageParam, signal }) => api<Sent[]>(`/api/chat/history?limit=${page}${pageParam ? `&before=${encodeURIComponent(pageParam)}` : ''}`, { signal }),
  initialPageParam: '',
  getNextPageParam: (last: Sent[]) => (last.length < page ? undefined : last.at(-1)?.at),
  staleTime: 60_000,
})

const texts = (data: InfiniteData<Sent[]> | undefined) => data?.pages.flatMap((p) => p.map((m) => m.text)) ?? []

/** The person's messages in their other chats, newest first: asked for the first time ↑ needs them. */
export function useOlderMessages(): Older {
  const queryClient = useQueryClient()
  const [on, setOn] = useState(false)
  const q = useInfiniteQuery({ ...olderQuery, enabled: on })
  const items = useMemo(() => texts(q.data), [q.data])
  const { data, hasNextPage, isFetching, isError, fetchNextPage } = q
  const load = useCallback(async () => {
    if (!on) {
      setOn(true)
      return texts(await queryClient.fetchInfiniteQuery(olderQuery))
    }
    return texts(hasNextPage ? (await fetchNextPage()).data : data)
  }, [on, queryClient, hasNextPage, fetchNextPage, data])
  return { items, done: on && !isFetching && (isError || !hasNextPage), load }
}

/** The caret's row in the box, as it is drawn (a long line wraps): measured on a copy laid out the same way. */
function caretRows(el: HTMLTextAreaElement, at: number): { first: boolean; last: boolean } {
  if (!el.value) return { first: true, last: true }
  const style = getComputedStyle(el)
  const copy = document.createElement('div')
  for (const p of ['direction', 'fontFamily', 'fontSize', 'fontStyle', 'fontWeight', 'fontStretch', 'letterSpacing', 'lineHeight', 'textTransform', 'textIndent', 'wordSpacing', 'tabSize', 'paddingLeft', 'paddingRight'] as const)
    copy.style[p] = style[p]
  Object.assign(copy.style, {
    position: 'absolute',
    visibility: 'hidden',
    top: '0',
    left: '-9999px',
    boxSizing: 'content-box',
    whiteSpace: 'pre-wrap',
    overflowWrap: 'break-word',
    // The width the text wraps at: the box's inside, without its scroll bar.
    width: `${Math.max(0, el.clientWidth - parseFloat(style.paddingLeft || '0') - parseFloat(style.paddingRight || '0'))}px`,
  })
  const marks = [0, 1, 2].map(() => document.createElement('span'))
  copy.append(marks[0]!, el.value.slice(0, at), marks[1]!, el.value.slice(at), marks[2]!)
  document.body.append(copy)
  try {
    const [start, caret, end] = marks.map((m) => m.offsetTop)
    return { first: caret! <= start!, last: caret! >= end! }
  } finally {
    copy.remove()
  }
}

/**
 * ↑ and ↓ in the message box, as Claude's apps have them: with the caret on the
 * first line (an empty box too), ↑ puts the message sent before in the box, then
 * older ones (this chat's, then the person's others); ↓ goes back toward the
 * newest and finally to what was being typed, kept. Esc goes straight back to
 * it. In several lines, a recalled message's too, ↑ and ↓ move between them
 * first. A recalled line that wraps steps on at once, until the caret moves
 * into it or it is edited (a copy: sent, it is a new message). Keys with Shift,
 * Ctrl, Alt or ⌘, a selection, and keys that compose (an IME) are left to the
 * box.
 */
export function useRecall({
  area,
  setText,
  source,
  enabled = true,
  onRecall,
}: {
  area: RefObject<HTMLTextAreaElement | null>
  setText: (text: string) => void
  source?: RecallSource
  enabled?: boolean
  /** A message was put in the box (a menu its text would open stays shut). */
  onRecall?: (text: string) => void
}) {
  const older = source?.older
  const list = useMemo(() => recallList(source?.here ?? [], older?.items ?? []), [source?.here, older?.items])
  /** Which message is in the box: -1 for what was being typed. */
  const [at, setAt] = useState(-1)
  const draft = useRef('')
  /** ↑ past what has come: the place it goes to once the next page does (any other key drops it). */
  const wanted = useRef<number | null>(null)
  /** The caret goes to the end of what was put in the box, once it is drawn. */
  const [placed, setPlaced] = useState(0)

  useLayoutEffect(() => {
    if (!placed) return
    const el = area.current
    el?.setSelectionRange(el.value.length, el.value.length)
  }, [placed, area])

  const show = useCallback(
    (i: number, from: string[]) => {
      const t = i < 0 ? draft.current : from[i]!
      setAt(i)
      setText(t)
      onRecall?.(t)
      setPlaced((n) => n + 1)
    },
    [setText, onRecall],
  )

  const onKeyDown = (e: KeyboardEvent<HTMLTextAreaElement>): boolean => {
    const waiting = wanted.current !== null
    if (e.key !== 'ArrowUp') wanted.current = null
    if (!enabled || e.altKey || e.ctrlKey || e.metaKey || e.shiftKey || e.nativeEvent.isComposing || e.keyCode === 229) return false
    const el = e.currentTarget
    if (e.key === 'Escape') {
      if (at < 0 && !waiting) return false
      e.preventDefault()
      show(-1, list)
      return true
    }
    if ((e.key !== 'ArrowUp' && e.key !== 'ArrowDown') || el.selectionStart !== el.selectionEnd) return false
    const caret = el.selectionStart
    // A message of one line recalled, untouched, the caret where it was put: the arrows step on at once, wrapped or not.
    const untouched = at >= 0 && el.value === list[at] && caret === el.value.length && !el.value.includes('\n')
    if (e.key === 'ArrowUp') {
      // Nothing older: the box's own Up (the caret to the start).
      if (!waiting && at + 1 >= list.length && (!older || older.done)) return false
      if (!untouched && (el.value.lastIndexOf('\n', caret - 1) >= 0 || !caretRows(el, caret).first)) return false
      e.preventDefault()
      if (waiting) return true
      if (at < 0) draft.current = el.value
      const next = at + 1
      if (next < list.length) {
        show(next, list)
        return true
      }
      const here = source?.here ?? []
      wanted.current = next
      older!.load().then(
        (items) => {
          if (wanted.current !== next) return
          wanted.current = null
          const fresh = recallList(here, items)
          if (next < fresh.length) show(next, fresh)
        },
        () => {
          if (wanted.current === next) wanted.current = null
        },
      )
      return true
    }
    if (at < 0 || (!untouched && (el.value.indexOf('\n', caret) >= 0 || !caretRows(el, caret).last))) return false
    e.preventDefault()
    show(at - 1, list)
    return true
  }

  /** After sending: the next ↑ starts again from the newest. */
  const reset = useCallback(() => {
    setAt(-1)
    wanted.current = null
    draft.current = ''
  }, [])

  return { onKeyDown, reset, recalled: at >= 0 }
}
