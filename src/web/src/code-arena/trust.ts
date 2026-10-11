// Whether the person trusts this folder, as VS Code asks: its own programs (a project's Prettier in node_modules, the
// commands of .vscode/tasks.json and launch.json, language servers) run only then. Asked in a dialog, kept on this
// computer for this folder (never in the project).

interface Ask {
  why: string
  done: (trusted: boolean) => void
}

let current: Ask | null = null
const listeners = new Set<() => void>()
const changed = () => listeners.forEach((l) => l())

export const trustAsked = () => current
export function onTrustAsk(f: () => void) {
  listeners.add(f)
  return () => {
    listeners.delete(f)
  }
}

export function answerTrust(trusted: boolean) {
  const a = current
  current = null
  changed()
  a?.done(trusted)
}

/** Asks whether the folder is trusted (why: what wants to run); true when the person trusts it. */
export function askTrust(why: string): Promise<boolean> {
  return new Promise((resolve) => {
    current?.done(false)
    current = { why, done: resolve }
    changed()
  })
}
