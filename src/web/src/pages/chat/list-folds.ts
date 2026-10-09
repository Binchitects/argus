import { useSyncExternalStore } from 'react'
import { useOutletContext } from 'react-router'
import type { Me } from '@/lib/api'

/**
 * What a person folded in the chat list: the whole list beside the chat (wide
 * screens), and its groups by their heading (Assistants, Today, Yesterday, a
 * month...). Remembered on this browser for that person: someone else signing in
 * here has their own. Every list on the page (beside the chat, the phone's panel)
 * shows the same.
 */
interface Folds {
  list: boolean
  groups: string[]
}

const none: Folds = { list: false, groups: [] }
/** Month headings pile up over the years: the last ones folded are enough. */
const MAX_GROUPS = 40

const listeners = new Set<() => void>()
// Where folds live when the browser keeps nothing (a private window): this page only.
const memory = new Map<string, string>()
// The parsed value of each key, kept while its text is unchanged (React needs the same object).
const parsed = new Map<string, { raw: string | null; folds: Folds }>()

function load(key: string): string | null {
  try {
    return localStorage.getItem(key)
  } catch {
    return memory.get(key) ?? null
  }
}

function store(key: string, value: string) {
  try {
    localStorage.setItem(key, value)
  } catch {
    memory.set(key, value)
  }
}

function readFolds(key: string): Folds {
  const raw = load(key)
  const known = parsed.get(key)
  if (known && known.raw === raw) return known.folds
  let folds = none
  try {
    const v: unknown = raw ? JSON.parse(raw) : null
    if (v && typeof v === 'object') {
      const { list, groups } = v as { list?: unknown; groups?: unknown }
      folds = { list: list === true, groups: Array.isArray(groups) ? groups.filter((g): g is string => typeof g === 'string') : [] }
    }
  } catch {
    // not ours, or cut short: nothing folded
  }
  parsed.set(key, { raw, folds })
  return folds
}

function subscribe(on: () => void) {
  listeners.add(on)
  // Another tab of the same browser folds too.
  window.addEventListener('storage', on)
  return () => {
    listeners.delete(on)
    window.removeEventListener('storage', on)
  }
}

/** The chat list's folds for the person signed in, and how to change them. */
export function useListFolds() {
  const me = useOutletContext<Me | undefined>()
  const key = `chat-list:${me?.id ?? ''}`
  const folds = useSyncExternalStore(subscribe, () => readFolds(key), () => none)
  const save = (change: (f: Folds) => Folds) => {
    store(key, JSON.stringify(change(readFolds(key))))
    for (const l of listeners) l()
  }
  return {
    listFolded: folds.list,
    setListFolded: (list: boolean) => save((f) => ({ ...f, list })),
    isFolded: (group: string) => folds.groups.includes(group),
    toggleGroup: (group: string) =>
      save((f) => ({ ...f, groups: f.groups.includes(group) ? f.groups.filter((g) => g !== group) : [...f.groups, group].slice(-MAX_GROUPS) })),
  }
}
