import { createContext, use } from 'react'

/** A place in a file an answer cites: the file's path in the folder, and its lines (and column) when given. */
export interface FileRef {
  path: string
  line?: number
  end?: number
  column?: number
}

/**
 * Where the files cited in answers open (Code Arena's IDE: in its editor). Only `known` paths, files of the folder,
 * are links: anything else the model wrote stays text. Arena's web chat has none, and its answers have no such links.
 */
export interface FileRefsApi {
  /** The path as the folder has it (`./`, `\` and the folder's own path taken off); null: not a file of the folder. */
  known: (path: string) => string | null
  open: (ref: FileRef) => void
}

export const FileRefs = createContext<FileRefsApi | null>(null)

export const useFileRefs = () => use(FileRefs)

const path = String.raw`(?<path>[^\s:#()'"\x60]+?)`
// path#L12, path#L12-L30; path(12) and path(12,5) (compilers); path:12, path:12-30, path:12:5; the path alone.
const forms = [
  new RegExp(String.raw`^${path}#L(?<line>\d+)(?:-L?(?<end>\d+))?$`),
  new RegExp(String.raw`^${path}\((?<line>\d+)(?:,(?<column>\d+))?\)$`),
  new RegExp(String.raw`^${path}:(?<line>\d+)(?:-(?<end>\d+)|:(?<column>\d+))?:?$`),
  new RegExp(String.raw`^${path}$`),
]

/** A citation of a file (`src/app.ts:12`, `src/app.ts:12-30`, `src/app.ts#L12`, `Program.cs(44,13)`, a path); null: not one. */
export function parseRef(text: string): FileRef | null {
  const t = text.trim()
  if (!t || t.length > 400 || /^[a-z][a-z0-9+.-]*:\/\//i.test(t)) return null
  for (const form of forms) {
    const g = form.exec(t)?.groups
    if (!g?.path) continue
    const line = g.line ? Number(g.line) : undefined
    const end = g.end ? Number(g.end) : undefined
    return {
      path: g.path,
      ...(line && { line }),
      ...(end && line && end >= line && { end }),
      ...(g.column && line && { column: Number(g.column) }),
    }
  }
  return null
}

/** How a citation reads: its path and lines. */
export const refLabel = (r: FileRef) => r.path + (r.line ? `:${r.line}${r.end && r.end !== r.line ? `-${r.end}` : ''}` : '')

/** A citation in running text: a path with an extension (or a folder in it), with its lines or not. */
const inText = /(?<![\w/\\.@-])((?:[\w.-]+[/\\])+[\w.-]+|[\w-][\w.-]*\.[A-Za-z][A-Za-z0-9]{0,7})(?::\d+(?:-\d+|:\d+)?|#L\d+(?:-L?\d+)?|\(\d+(?:,\d+)?\))?(?![\w/\\])/g

/** The citations of the folder's files in a text (a terminal's line, an answer's text): where each starts, as written, and its place. */
export function findRefs(text: string, refs: FileRefsApi): { at: number; text: string; ref: FileRef }[] {
  return [...text.matchAll(inText)].flatMap((m) => {
    const ref = resolve(m[0], refs)
    return ref ? [{ at: m.index, text: m[0], ref }] : []
  })
}

/** The places of the links tagged here: only these open (an answer's HTML may carry a data-ref of its own). */
const tagged = new WeakMap<Element, FileRef>()

/** Marks an element as a link to the place cited. */
function mark(el: HTMLElement, ref: FileRef) {
  tagged.set(el, ref)
  el.dataset.ref = JSON.stringify(ref)
  el.classList.add('md-ref')
  el.setAttribute('role', 'link')
  el.tabIndex = 0
  el.title = `Open ${refLabel(ref)} in the editor`
}

/** The citation `text` makes, when its file is the folder's. */
function resolve(text: string, refs: FileRefsApi): FileRef | null {
  const ref = parseRef(text)
  const known = ref && refs.known(ref.path)
  return ref && known ? { ...ref, path: known } : null
}

/**
 * Makes the citations of the folder's files in an answer (rendered Markdown) links: inline code, relative links, and paths
 * in its text. Code blocks are left as they are. Done again each time the answer's text changes (it is drawn anew).
 */
export function tagRefs(root: HTMLElement, refs: FileRefsApi) {
  for (const code of root.querySelectorAll<HTMLElement>('code:not(pre code):not([data-ref])')) {
    const ref = resolve(code.textContent ?? '', refs)
    if (ref) mark(code, ref)
  }
  for (const a of root.querySelectorAll<HTMLAnchorElement>('a[href]:not([data-ref])')) {
    const href = a.getAttribute('href') ?? ''
    if (/^([a-z][a-z0-9+.-]*:|#)/i.test(href)) continue
    let target = href
    try {
      target = decodeURIComponent(href)
    } catch {
      // As written.
    }
    const ref = resolve(target, refs) ?? resolve(a.textContent ?? '', refs)
    if (ref) mark(a, ref)
    // A link to a path that is no file of the folder goes nowhere (the page would leave the IDE).
    else a.dataset.local = 'true'
  }
  const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT, {
    acceptNode: (n) => (n.parentElement?.closest('pre, code, a, .md-ref, [data-ref]') ? NodeFilter.FILTER_REJECT : NodeFilter.FILTER_ACCEPT),
  })
  const texts: Text[] = []
  while (walker.nextNode()) texts.push(walker.currentNode as Text)
  for (const node of texts) {
    const value = node.nodeValue ?? ''
    const found = findRefs(value, refs)
    if (found.length === 0) continue
    const parts = document.createDocumentFragment()
    let from = 0
    for (const f of found) {
      parts.append(value.slice(from, f.at))
      const span = document.createElement('span')
      span.textContent = f.text
      mark(span, f.ref)
      parts.append(span)
      from = f.at + f.text.length
    }
    parts.append(value.slice(from))
    node.replaceWith(parts)
  }
}

/** Opens the citation clicked (or chosen with Enter); a link to no file of the folder does nothing. */
export function openRef(e: { target: EventTarget | null; preventDefault: () => void }, refs: FileRefsApi) {
  const el = e.target instanceof Element ? e.target.closest<HTMLElement>('[data-ref], a[data-local]') : null
  if (!el) return
  e.preventDefault()
  const ref = tagged.get(el)
  const path = ref && refs.known(ref.path)
  if (ref && path) refs.open({ ...ref, path })
}
