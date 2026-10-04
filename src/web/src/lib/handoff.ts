/**
 * What the browser extension hands over: a page's title, address and text, or a
 * selection of it. It comes in /ask's fragment (#title=…&url=…&text=…), which the
 * browser never sends to a server, and is kept in this tab while the person signs in.
 */
export interface Quote {
  title: string
  url: string
  text: string
  /** A selection on the page, not the whole page. */
  selection: boolean
}

const KEY = 'ask-quote'

/** The quote in a fragment; null when it holds none. */
export function parseHandoff(hash: string): Quote | null {
  const p = new URLSearchParams(hash.replace(/^#/, ''))
  const text = p.get('text')?.trim()
  if (!text) return null
  return { title: p.get('title')?.trim() || 'A page', url: p.get('url')?.trim() ?? '', text, selection: p.get('sel') === '1' }
}

/** Keeps a quote handed over in /ask#… for the page (so it survives signing in) and takes it out of the address. */
export function keepHandoff(location: Pick<Location, 'pathname' | 'hash'> = window.location, history: Pick<History, 'replaceState'> = window.history) {
  if (location.pathname !== '/ask' || !location.hash) return
  const quote = parseHandoff(location.hash)
  try {
    if (quote) sessionStorage.setItem(KEY, JSON.stringify(quote))
  } catch {
    // No storage (a locked-down browser): the quote is lost on a sign-in, not otherwise.
  }
  history.replaceState(null, '', '/ask')
  if (quote) pending = quote
}

let pending: Quote | null = null

/** The quote waiting to be asked about, if any. */
export function readQuote(): Quote | null {
  try {
    const kept = sessionStorage.getItem(KEY)
    if (kept) return JSON.parse(kept) as Quote
  } catch {
    // fall back to the one kept in memory
  }
  return pending
}

/** Forgets the quote: it was asked about. */
export function clearQuote() {
  pending = null
  try {
    sessionStorage.removeItem(KEY)
  } catch {
    // nothing kept
  }
}

/** The quote as a Markdown file to attach: where it is from, then its words. */
export function quoteFile(q: Quote): File {
  const name = q.title.replace(/[\\/:*?"<>|\s]+/g, ' ').trim().slice(0, 80) || 'page'
  const body = `# ${q.title}\n\n${q.url ? `${q.selection ? 'A selection of' : 'The text of'} ${q.url}\n\n` : ''}${q.text}\n`
  return new File([body], `${q.selection ? 'Selection' : 'Page'} - ${name}.md`, { type: 'text/markdown' })
}
