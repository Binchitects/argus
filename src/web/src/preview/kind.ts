/**
 * What the chat can show live: a page, a picture, a diagram or a React
 * component. Shared by the chat (which offers Preview) and the runner at
 * /preview.html (which draws it, sandboxed, with no network).
 */
export type PreviewKind = 'html' | 'svg' | 'mermaid' | 'react'

export const previewLabels: Record<PreviewKind, string> = { html: 'page', svg: 'picture', mermaid: 'diagram', react: 'component' }

const byLang: Record<string, PreviewKind> = { html: 'html', htm: 'html', xhtml: 'html', svg: 'svg', mermaid: 'mermaid', mmd: 'mermaid', jsx: 'react', tsx: 'react' }

/**
 * The kind of a fence, from its own language (before any aliasing: `jsx` is a
 * component, `javascript` is not), its file name, and for bare `xml` what it holds.
 */
export function previewKindOf(rawLang: string | null | undefined, name: string | null | undefined, code: string): PreviewKind | null {
  const lang = (rawLang ?? '').trim().toLowerCase()
  const ext = name?.includes('.') ? name.split('.').pop()!.toLowerCase() : ''
  const kind = byLang[lang] ?? byLang[ext]
  if (kind) return kind
  if (lang === 'xml' || lang === '') {
    const head = code.trimStart().slice(0, 200).toLowerCase()
    if (head.startsWith('<svg') || (head.startsWith('<?xml') && head.includes('<svg'))) return 'svg'
    if (head.startsWith('<!doctype html') || head.startsWith('<html')) return 'html'
  }
  return null
}

/**
 * Messages between the chat and the runner. Drawn inline (a diagram in an answer),
 * the runner fits its content and reports its height, so the frame grows to fit.
 */
export type ToRunner = { type: 'render'; kind: PreviewKind; code: string; theme: 'light' | 'dark'; inline?: boolean }
export type FromRunner =
  | { type: 'ready' }
  | { type: 'rendered' }
  | { type: 'error'; message: string }
  | { type: 'blocked'; uri: string }
  | { type: 'size'; height: number }

export function isFromRunner(data: unknown): data is FromRunner {
  const d = data as { type?: unknown; height?: unknown } | null
  const t = d?.type
  return t === 'ready' || t === 'rendered' || t === 'error' || t === 'blocked' || (t === 'size' && typeof d?.height === 'number' && Number.isFinite(d.height))
}

export function isToRunner(data: unknown): data is ToRunner {
  const d = data as Partial<ToRunner> | null
  return d?.type === 'render' && typeof d.code === 'string' && (d.kind === 'html' || d.kind === 'svg' || d.kind === 'mermaid' || d.kind === 'react')
}
