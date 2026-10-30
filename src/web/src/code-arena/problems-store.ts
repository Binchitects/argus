import { useQuery } from '@tanstack/react-query'
import type * as Monaco from 'monaco-editor'
import { useSyncExternalStore } from 'react'
import { whenMonaco } from './editor-state'
import { globMatch } from './glob'
import { nameOf, problemsQuery, type CheckProblem } from './ide-api'
import { languageServers, useLanguageServers } from './lsp/servers'

/** Where a problem comes from: the project's check (its tasks' and the security review's with it), a language server, or the editor's own service for a language. */
export type ProblemSource = 'check' | 'server' | 'editor'

/** A problem as Problems lists it and the bars count it; a server's or the editor's says whose. */
export type Problem = CheckProblem & { source: ProblemSource; server?: string }

/**
 * The editor's own services whose markers are problems: TypeScript's and JavaScript's, JSON's, CSS's and HTML's. The check's
 * marks and the language servers' are counted from where they come, not again from the editor.
 */
const editorOwners: Record<string, string> = {
  typescript: 'TypeScript', javascript: 'JavaScript', json: 'JSON', css: 'CSS', scss: 'SCSS', less: 'Less', html: 'HTML',
}

/** The editor's markers of those services, read again whenever they change (once Monaco is loaded: nothing before). */
class EditorMarkers {
  private problems: Problem[] = []
  private readonly listeners = new Set<() => void>()
  private started = false

  subscribe = (listener: () => void) => {
    this.listeners.add(listener)
    if (!this.started) {
      this.started = true
      whenMonaco((m) => {
        const read = () => {
          this.problems = this.read(m.monaco, m.pathOf)
          for (const l of this.listeners) l()
        }
        m.monaco.editor.onDidChangeMarkers(read)
        read()
      })
    }
    return () => void this.listeners.delete(listener)
  }

  snapshot = () => this.problems

  private read(monaco: typeof Monaco, pathOf: (uri: Monaco.Uri) => string | null): Problem[] {
    const out: Problem[] = []
    for (const marker of monaco.editor.getModelMarkers({})) {
      const server = editorOwners[marker.owner]
      const path = server ? pathOf(marker.resource) : null
      // Hints are the editor's quiet suggestions (an unused name greyed out): not problems.
      if (!server || path === null || marker.severity === monaco.MarkerSeverity.Hint || !monaco.editor.getModel(marker.resource)) continue
      const severity = marker.severity === monaco.MarkerSeverity.Error ? 'error' : marker.severity === monaco.MarkerSeverity.Warning ? 'warning' : 'info'
      const code = marker.code === undefined ? null : typeof marker.code === 'string' ? marker.code : marker.code.value
      out.push({ source: 'editor', server, path, line: marker.startLineNumber, column: marker.startColumn, severity, code, message: marker.message })
    }
    return out
  }
}

const editorMarkers = new EditorMarkers()

/** Every problem the IDE knows now: the check's, the language servers' and the editor's own services'. */
export function useProblems(): Problem[] {
  const check = useQuery(problemsQuery).data
  useLanguageServers()
  const editor = useSyncExternalStore(editorMarkers.subscribe, editorMarkers.snapshot)
  return [
    ...(check?.problems ?? []).map((p): Problem => ({ ...p, source: 'check' })),
    ...languageServers.problems().map((p): Problem => ({ ...p, source: 'server' })),
    ...editor,
  ]
}

/** How many of each severity. */
export function countProblems(problems: Problem[]) {
  const n = { error: 0, warning: 0, info: 0 }
  for (const p of problems) n[p.severity]++
  return n
}

/**
 * Problems' filter, as VS Code reads it: terms split by commas; a term with `*`, `?` or `/` is a glob of the file's path (or
 * of its name, when it has no `/`), any other the text of the message, its code, its file or its source; a term starting
 * with `!` leaves out what it matches.
 */
export function problemFilter(text: string): (p: Problem) => boolean {
  const terms = text.split(',').map((t) => t.trim()).filter(Boolean)
  const leaveOut = terms.filter((t) => t.startsWith('!')).map((t) => t.slice(1).trim()).filter(Boolean)
  const keep = terms.filter((t) => !t.startsWith('!'))
  if (keep.length === 0 && leaveOut.length === 0) return () => true
  const hit = (term: string, p: Problem) =>
    /[*?/]/.test(term)
      ? globMatch(term, p.path) || (!term.includes('/') && globMatch(term, nameOf(p.path)))
      : `${p.message} ${p.code ?? ''} ${p.path} ${p.server ?? ''}`.toLowerCase().includes(term.toLowerCase())
  return (p) => (keep.length === 0 || keep.some((t) => hit(t, p))) && !leaveOut.some((t) => hit(t, p))
}

/** A problem as Copy writes it: where, how bad, its code and what it says. */
export const problemLine = (p: Problem) => `${p.path}:${p.line}:${p.column} ${p.severity}${p.code ? ` ${p.code}` : ''} ${p.message}`
