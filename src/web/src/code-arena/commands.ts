import { useEffect, useLayoutEffect, useRef, useSyncExternalStore } from 'react'

/**
 * The workbench's commands, under VS Code's ids: the palette lists them, keys and keybindings.json name them, the editor
 * runs them (Monaco is given each). A part of the page registers its own while it is there.
 */
export interface Command {
  id: string
  title: string
  /** Shown before the title in the palette: "View", "File", "Preferences"… */
  category?: string
  run: (args?: unknown) => unknown
  /** False: not offered in the palette now (it would do nothing). */
  enabled?: () => boolean
  /** Not in the palette at all (a key's own, an internal one). */
  hidden?: boolean
}

const registry = new Map<string, Command>()
const listeners = new Set<() => void>()
let version = 0

const changed = () => {
  version++
  for (const l of listeners) l()
}

/** Registers commands; the function takes them away again (only those still the same). */
export function register(...list: Command[]): () => void {
  for (const c of list) registry.set(c.id, c)
  changed()
  return () => {
    for (const c of list) if (registry.get(c.id) === c) registry.delete(c.id)
    changed()
  }
}

export const getCommand = (id: string) => registry.get(id)
export const allCommands = () => [...registry.values()]
export const hasCommand = (id: string) => registry.has(id)

/** Runs a command by its id; false when there is none (or it is off now). */
export function runCommand(id: string, args?: unknown): boolean {
  const c = registry.get(id)
  if (!c || (c.enabled && !c.enabled())) return false
  void Promise.resolve()
    .then(() => c.run(args))
    .catch((e: unknown) => console.error(`Command ${id} failed`, e))
  return true
}

export function onCommandsChange(f: () => void) {
  listeners.add(f)
  return () => {
    listeners.delete(f)
  }
}

/** The registry's version: changes whenever a command comes or goes. */
export function useCommandsVersion() {
  return useSyncExternalStore(onCommandsChange, () => version)
}

/**
 * Commands of a part of the page, there while it is: each runs the latest of its function (no registering again at each
 * render).
 */
export function useCommands(list: Command[]) {
  const latest = useRef(list)
  useLayoutEffect(() => {
    latest.current = list
  })
  const ids = list.map((c) => c.id).join('\n')
  useEffect(() => {
    const own = ids.split('\n').map((id): Command => {
      const at = () => latest.current.find((c) => c.id === id)!
      const first = at()
      return {
        id,
        title: first.title,
        category: first.category,
        hidden: first.hidden,
        run: (args) => at().run(args),
        enabled: () => {
          const c = latest.current.find((x) => x.id === id)
          return !!c && (!c.enabled || c.enabled())
        },
      }
    })
    return register(...own)
  }, [ids])
}
