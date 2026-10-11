// The text to compare with when the browser does not let the page read the clipboard: asked for in a dialog.

interface Ask {
  title: string
  done: (text: string | undefined) => void
}

let current: Ask | null = null
const listeners = new Set<() => void>()
const changed = () => listeners.forEach((l) => l())

export const asked = () => current
export function onAsk(f: () => void) {
  listeners.add(f)
  return () => {
    listeners.delete(f)
  }
}

export function answerPaste(text: string | undefined) {
  const a = current
  current = null
  changed()
  a?.done(text)
}

/** The clipboard's text: read when the browser lets the page, else pasted by the person in a dialog. */
export async function clipboardText(title: string): Promise<string | undefined> {
  try {
    const text = await navigator.clipboard?.readText()
    if (typeof text === 'string') return text
  } catch {
    // Refused (no permission, not a secure page): the person pastes it.
  }
  return new Promise((resolve) => {
    current?.done(undefined)
    current = { title, done: resolve }
    changed()
  })
}
