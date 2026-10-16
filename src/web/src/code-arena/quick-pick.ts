import type { ReactNode } from 'react'

/** One choice of a quick pick: what it shows, and anything the caller wants back. */
export interface PickItem<T = unknown> {
  id: string
  label: string
  description?: string
  detail?: string
  /** The group it is listed under ("recently used", "detected"…). */
  group?: string
  icon?: ReactNode
  value?: T
}

export interface Open {
  title: string
  placeholder: string
  items: PickItem[]
  /** The id chosen at first. */
  active?: string
  /** Typing narrows the items (true), or the caller lists what fits (false: match). */
  filter: boolean
  /** With filter false: the items for what is typed, best first (a long list, ranked and cut, as Quick Open's). */
  match?: (typed: string) => PickItem[]
  /** The text in the box at first (an input box's value now), selected. */
  value?: string
  /** Nothing typed can be taken too (free is asked for ""): an input box's empty answer. */
  empty?: boolean
  /** Text typed that matches no item can be taken as it is (a name, a value). */
  free?: (typed: string) => PickItem | null
  /** What is typed is a secret: shown as dots, never in the list. */
  password?: boolean
  /** Told of the item chosen in the list as it changes (the Color Theme picker shows it at once). */
  onActive?: (item: PickItem | undefined) => void
  done: (item: PickItem | undefined) => void
}

let open: Open | null = null
const listeners = new Set<() => void>()
const changed = () => listeners.forEach((l) => l())

/** The pick shown now, for its host. */
export const currentPick = () => open
export function onPick(f: () => void) {
  listeners.add(f)
  return () => {
    listeners.delete(f)
  }
}

/** Ends a pick with the item chosen (none: cancelled). */
export function finishPick(o: Open, item: PickItem | undefined) {
  if (open === o) {
    open = null
    changed()
  }
  o.done(item)
}

/**
 * A list to choose one from, as VS Code's quick pick: typing narrows it, Enter takes the one chosen, Escape takes none.
 * One at a time: a second replaces the first (which answers undefined).
 */
export function quickPick<T>(o: {
  title: string
  placeholder: string
  items: PickItem<T>[]
  active?: string
  free?: (typed: string) => PickItem<T> | null
  password?: boolean
  onActive?: (item: PickItem<T> | undefined) => void
  /** The items for what is typed, in place of narrowing `items` by its words. */
  match?: (typed: string) => PickItem<T>[]
  value?: string
  empty?: boolean
}): Promise<PickItem<T> | undefined> {
  return new Promise((resolve) => {
    open?.done(undefined)
    open = { ...o, filter: !o.match, done: resolve as (item: PickItem | undefined) => void } as Open
    changed()
  })
}

/**
 * A line of text asked for, as VS Code's input box: the value now in the box at first, what is typed taken as it is (an
 * empty answer too); undefined when the person cancels.
 */
export function inputBox(o: { title: string; placeholder: string; value?: string; validate?: (v: string) => string | null; password?: boolean }): Promise<string | undefined> {
  return quickPick<string>({
    title: o.title,
    placeholder: o.placeholder,
    password: o.password,
    value: o.value,
    empty: true,
    items: [],
    free: (typed) => {
      const why = o.validate?.(typed)
      if (why) return { id: 'invalid', label: why }
      return { id: 'typed', label: o.password ? '•'.repeat(Math.min(typed.length, 24)) : typed || 'Nothing (empty)', description: 'Press Enter to use it', value: typed }
    },
  }).then((item) => (item && item.id !== 'invalid' ? item.value : undefined))
}
