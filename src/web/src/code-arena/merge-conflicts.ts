import { api } from './http'

// Conflict markers as git writes them, and as VS Code's merge-conflict support reads them:
//   <<<<<<< HEAD            the current change (ours) follows
//   ||||||| base            (with merge.conflictStyle diff3 or zdiff3) the common ancestor follows
//   =======                 the incoming change (theirs) follows
//   >>>>>>> feature         the end
// Lines are 1-based; a range is [start, end): its first line and the line after its last.

export interface LineRange {
  start: number
  end: number
}

export interface Conflict {
  /** The <<<<<<< line, and the >>>>>>> line. */
  start: number
  end: number
  /** The ======= line. */
  middle: number
  /** The ||||||| line, when the conflict has the common ancestor. */
  baseMarker: number | null
  current: LineRange
  incoming: LineRange
  base: LineRange | null
  /** What follows <<<<<<< and >>>>>>> (HEAD, a branch, a commit and its subject). */
  currentLabel: string
  incomingLabel: string
}

/** What a conflict is resolved to. */
export type Choice = 'current' | 'incoming' | 'both' | 'base' | 'none'

/** A text's lines with their line endings ("a\n", "b\r\n", the last perhaps with none). */
export function linesOf(text: string): string[] {
  const out: string[] = []
  let from = 0
  for (let i = text.indexOf('\n'); i >= 0; i = text.indexOf('\n', from)) {
    out.push(text.slice(from, i + 1))
    from = i + 1
  }
  if (from < text.length) out.push(text.slice(from))
  return out
}

const bare = (line: string) => line.replace(/\r?\n$/, '')
const marker = (line: string, mark: string) => {
  const l = bare(line)
  return l === mark || l.startsWith(`${mark} `)
}
const label = (line: string, mark: string) => bare(line).slice(mark.length).trim()

/** The conflicts in a text, in order; markers that do not make a whole conflict are left as they are. */
export function findConflicts(text: string): Conflict[] {
  if (!text.includes('<<<<<<<')) return []
  const lines = linesOf(text)
  const out: Conflict[] = []
  let open: { start: number; baseMarker: number | null; middle: number | null } | null = null
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i]!
    const n = i + 1
    if (marker(line, '<<<<<<<')) open = { start: n, baseMarker: null, middle: null }
    else if (!open) continue
    else if (open.middle === null && open.baseMarker === null && marker(line, '|||||||')) open.baseMarker = n
    else if (open.middle === null && bare(line) === '=======') open.middle = n
    else if (open.middle !== null && marker(line, '>>>>>>>')) {
      const { start, baseMarker, middle } = open
      out.push({
        start,
        end: n,
        middle,
        baseMarker,
        current: { start: start + 1, end: baseMarker ?? middle },
        base: baseMarker === null ? null : { start: baseMarker + 1, end: middle },
        incoming: { start: middle + 1, end: n },
        currentLabel: label(lines[start - 1]!, '<<<<<<<'),
        incomingLabel: label(line, '>>>>>>>'),
      })
      open = null
    }
  }
  return out
}

const take = (lines: string[], r: LineRange | null) => (r ? lines.slice(r.start - 1, r.end - 1) : [])

/** The line ending the text uses (its first), for a part that ends with none (the last line of a text). */
const eolOf = (lines: string[]) => (lines.find((l) => l.endsWith('\n'))?.endsWith('\r\n') ? '\r\n' : '\n')

/** The lines a conflict is resolved to: one side, both (current first, as VS Code's Accept Both), the ancestor's, or none. */
export function resolution(lines: string[], c: Conflict, choice: Choice): string[] {
  const current = take(lines, c.current)
  const incoming = take(lines, c.incoming)
  if (choice === 'both') {
    // The current part's last line before the incoming one ends with a line break, whatever it was.
    const eol = eolOf(lines)
    return [...current.map((l, i) => (i === current.length - 1 && incoming.length > 0 && !l.endsWith('\n') ? l + eol : l)), ...incoming]
  }
  return choice === 'current' ? current : choice === 'incoming' ? incoming : choice === 'base' ? take(lines, c.base) : []
}

/**
 * A conflict resolved in a text: the text with the conflict's lines (its markers among them) as the choice has them.
 * The last line keeps the text's end: a conflict at the end of a file with no final line break leaves none.
 */
export function resolveText(text: string, c: Conflict, choice: Choice): string {
  const lines = linesOf(text)
  const into = resolution(lines, c, choice)
  const endsBare = c.end === lines.length && !lines[c.end - 1]!.endsWith('\n')
  if (endsBare && into.length > 0) into[into.length - 1] = bare(into[into.length - 1]!)
  return [...lines.slice(0, c.start - 1), ...into, ...lines.slice(c.end)].join('')
}

/** Every conflict resolved the same way (Accept All Current, Accept All Incoming, Accept All Both). */
export function resolveAll(text: string, choice: Choice): string {
  let out = text
  // From the last: the lines of those before stay where they are.
  for (const c of findConflicts(text).reverse()) out = resolveText(out, c, choice)
  return out
}

/** A side of a merge as a text: the conflicts' part of that side, all else as the file has it; and where each conflict's part is in it. */
export interface SideText {
  text: string
  /** Each conflict's lines in the side's text, in order. */
  ranges: LineRange[]
}

/**
 * The current and the incoming side as the merge editor shows them above the result: the file with each conflict
 * replaced by its current part (or its incoming part). Git's merge puts what both sides agree on outside the
 * markers, so each is the file as that side wants it.
 */
export function sideText(text: string, side: 'current' | 'incoming'): SideText {
  const lines = linesOf(text)
  const out: string[] = []
  const ranges: LineRange[] = []
  let at = 1
  for (const c of findConflicts(text)) {
    out.push(...lines.slice(at - 1, c.start - 1))
    const part = take(lines, c[side])
    ranges.push({ start: out.length + 1, end: out.length + 1 + part.length })
    out.push(...part)
    at = c.end + 1
  }
  out.push(...lines.slice(at - 1))
  return { text: out.join(''), ranges }
}

// ----- The merge editor's -----

/** A side of the merge as git names it: its branch (or what names its commit), commit and subject. */
export interface MergeSide {
  name: string
  commit: string
  short: string
  subject: string
}

/** What code-arena says of a file in conflict. */
export interface MergeInfo {
  path: string
  /** git has it unmerged still (its sides are in the index). */
  unmerged: boolean
  stages: number[]
  kind: 'merge' | 'rebase' | 'cherry-pick' | 'revert' | null
  current: MergeSide | null
  incoming: MergeSide | null
}

export const mergeInfoQuery = (path: string) => ({
  queryKey: ['code', 'git', 'merge', path] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<MergeInfo>(`/api/git/merge?path=${encodeURIComponent(path)}`, { signal }),
  retry: false,
})

/** Complete Merge: the file staged (refused while it has markers, unless anyway). */
export const completeMerge = (path: string, anyway: boolean) => api('/api/git/merge/complete', { body: { path, anyway } })

/** What is taken of a conflict, as the merge editor's checkboxes have it. */
export interface Taken {
  current: boolean
  incoming: boolean
}

/** A conflict's lines in the result as what is taken has them: both (the current first), one, or the conflict as it was. */
export function takenText(parts: { current: string; incoming: string; whole: string }, taken: Taken): string {
  if (taken.current && taken.incoming) return parts.current + (parts.current && !parts.current.endsWith('\n') ? '\n' : '') + parts.incoming
  return taken.current ? parts.current : taken.incoming ? parts.incoming : parts.whole
}

/** Each conflict's parts as the file had them when the merge editor opened. */
export function conflictParts(text: string, conflicts: Conflict[]) {
  const lines = linesOf(text)
  const of = (r: LineRange) => lines.slice(r.start - 1, r.end - 1).join('')
  return conflicts.map((c) => ({ current: of(c.current), incoming: of(c.incoming), whole: of({ start: c.start, end: c.end + 1 }) }))
}

