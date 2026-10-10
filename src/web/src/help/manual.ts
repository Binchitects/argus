import { lexer, type Token, type Tokens } from 'marked'
import admin from '@docs/admin.md?raw'
import architecture from '@docs/architecture.md?raw'
import argusBackup from '@docs/argus/backup-and-restore.md?raw'
import argusBranches from '@docs/argus/branches.md?raw'
import argusClients from '@docs/argus/clients.md?raw'
import argusGraph from '@docs/argus/graph.md?raw'
import argusPacks from '@docs/argus/knowledge-packs.md?raw'
import argusOverview from '@docs/argus/overview.md?raw'
import argusPgvector from '@docs/argus/pgvector-backend.md?raw'
import argus from '@docs/argus/README.md?raw'
import authentication from '@docs/authentication.md?raw'
import chat from '@docs/chat.md?raw'
import ci from '@docs/ci.md?raw'
import codeArena from '@docs/code-arena.md?raw'
import configuration from '@docs/configuration.md?raw'
import cpuTemperature from '@docs/cpu-temperature.md?raw'
import deployment from '@docs/deployment.md?raw'
import hermes from '@docs/hermes.md?raw'
import integrations from '@docs/integrations.md?raw'
import knowledge from '@docs/knowledge.md?raw'
import mcp from '@docs/mcp.md?raw'
import plugins from '@docs/plugins.md?raw'
import settings from '@docs/settings.md?raw'
import { Slugger } from './slug'

/**
 * The manual: the pages of docs/ that people and admins need, built into the web when it is
 * built (no network). The developers' pages stay in the repository: they are named in
 * notInTheManual, so a new page under docs/ is placed on purpose (manual.test.ts).
 */

export type Audience = 'everyone' | 'admins'

export interface ManualDoc {
  /** Its address: /help/<id>. A folder's README is the folder (argus). */
  id: string
  /** Its file under docs/. */
  file: string
  /** Its name in the contents. */
  title: string
  /** What it covers, in a line. */
  about: string
  audience: Audience
  text: string
}

export interface ManualGroup {
  title: string
  docs: ManualDoc[]
}

const doc = (file: string, title: string, about: string, audience: Audience, text: string): ManualDoc => ({
  id: file.replace(/(^|\/)README\.md$/, '').replace(/\.md$/, ''),
  file,
  title,
  about,
  audience,
  text,
})

/** The page-by-page help (help/topics.ts), a page of the manual too: /help/pages. */
export const PAGES_ID = 'pages'

export const manualGroups: ManualGroup[] = [
  {
    title: 'Using it',
    docs: [
      doc('chat.md', 'Chat', 'Models, thinking, files, tools, assistants, prompts, scheduled tasks and what each person sees.', 'everyone', chat),
      doc('integrations.md', 'Bots, email and the app', 'Slack, Mattermost and Teams bots, email in, the installed app with push notifications, the browser extension.', 'everyone', integrations),
      doc('mcp.md', 'Arena MCP', 'Your chat tools for your own agent, at /mcp, signed in with your API key.', 'everyone', mcp),
      doc('code-arena.md', 'Code Arena', 'Our own coding agent and its IDE: getting it, signing in, modes, tools and sessions.', 'everyone', codeArena),
      doc('ci.md', 'The API in CI', 'A review of each merge request and an explanation of each failed pipeline, from your own GitLab pipeline.', 'everyone', ci),
      doc('hermes.md', 'Hermes', 'Pointing Hermes at the model and at Argus.', 'everyone', hermes),
    ],
  },
  {
    title: 'Argus, the code index',
    docs: [
      doc('argus/overview.md', 'What Argus does', 'What it gives an agent, keeping the index current, knowledge packs.', 'everyone', argusOverview),
      doc('argus/graph.md', 'How repositories are linked', 'How sure each link is, its layers and evidence, and what the graph tools walk.', 'everyone', argusGraph),
      doc('argus/clients.md', 'Connecting an MCP client', 'Argus from any MCP client.', 'everyone', argusClients),
      doc('argus/README.md', 'Argus in the platform', 'Per-person access, choosing what is indexed, private CAs, the audit stream.', 'admins', argus),
      doc('argus/knowledge-packs.md', 'Knowledge packs', 'Building and publishing packs.', 'admins', argusPacks),
      doc('argus/branches.md', 'More than one branch', 'Indexing more than one branch of a repository.', 'admins', argusBranches),
      doc('argus/backup-and-restore.md', 'Backup and restore', 'What is worth keeping, and how to get it back.', 'admins', argusBackup),
      doc('argus/pgvector-backend.md', 'The pgvector backend', 'The optional Postgres backend for embeddings.', 'admins', argusPgvector),
    ],
  },
  {
    title: 'Running it',
    docs: [
      doc('admin.md', 'Admin, usage and cost', 'The admin area: people, groups, models, tools, dashboards, logs, alerts, usage and cost.', 'admins', admin),
      doc('settings.md', 'Settings', 'The Settings page: what applies at once or after a restart, secrets, what groups can set.', 'admins', settings),
      doc('authentication.md', 'Sign-in', 'Who signs in where: local accounts, the directory, company sign-in, SCIM, 2FA.', 'admins', authentication),
      doc('knowledge.md', 'Company knowledge', 'GitLab, Confluence, SharePoint, folders and websites, searched by each person within their rights.', 'admins', knowledge),
      doc('plugins.md', 'Plugins and APIs', 'Installing plugins, each person\'s own account, the manifest, a catalog.', 'admins', plugins),
      doc('deployment.md', 'Deploying', 'The samples and secrets, addresses, a host with no network, switching the model, the traps.', 'admins', deployment),
      doc('configuration.md', 'Configuration', 'Every .env variable and every file under deploy/config/.', 'admins', configuration),
      doc('architecture.md', 'Architecture', 'Every service, how a request flows through them, where state lives, what each failure looks like.', 'admins', architecture),
      doc('cpu-temperature.md', 'CPU temperature', 'How CPU temperature reaches the dashboards, on Linux and on Windows.', 'admins', cpuTemperature),
    ],
  },
]

/** Pages of docs/ kept out of the manual: the developers', and Argus run alone, without this platform. */
export const notInTheManual = [
  'README.md',
  'development.md',
  'testing.md',
  'plan.md',
  'argus/roadmap.md',
  'argus/standalone/architecture.md',
  'argus/standalone/configuration.md',
  'argus/standalone/operations.md',
]

export const manualDocs: ManualDoc[] = manualGroups.flatMap((g) => g.docs)

/**
 * Whether the manual lists a page for this person. It only tidies the manual: every page is in
 * the web's files, which load without a sign-in, so docs/ holds nothing confidential (docs/README.md).
 */
export function canRead(d: Pick<ManualDoc, 'audience'>, isAdmin: boolean): boolean {
  return d.audience === 'everyone' || isAdmin
}

export interface Heading {
  level: number
  text: string
  id: string
}

export interface Section {
  /** Its heading's anchor. */
  id: string
  title: string
  level: number
  /** Its words, without the Markdown. */
  text: string
}

export interface ParsedDoc {
  headings: Heading[]
  /** The page cut at its headings of levels 1 to 3, for search. */
  sections: Section[]
}

const entities: Record<string, string> = { '&amp;': '&', '&lt;': '<', '&gt;': '>', '&quot;': '"', '&#39;': "'" }

/** A heading's text as the page shows it. */
function inlineText(tokens: Token[] | undefined): string {
  return (tokens ?? [])
    .map((t) => {
      if ('tokens' in t && t.tokens?.length) return inlineText(t.tokens)
      if (t.type === 'html' || t.type === 'br') return ''
      return 'text' in t ? String(t.text) : ''
    })
    .join('')
    .replace(/&(amp|lt|gt|quot|#39);/g, (m) => entities[m])
}

/** Markdown to the words a search looks at. */
export function plainText(markdown: string): string {
  return markdown
    .replace(/^\s*(```|~~~).*$/gm, ' ')
    .replace(/^[\s|:-]*-{3,}[\s|:-]*$/gm, ' ')
    .replace(/!\[([^\]]*)\]\([^)]*\)/g, '$1')
    .replace(/\[([^\]]*)\]\([^)]*\)/g, '$1')
    .replace(/<[^>]+>/g, ' ')
    .replace(/^\s*(#{1,6}|>|[-*+]|\d+\.)\s+/gm, '')
    .replace(/[`*|]/g, ' ')
    .replace(/&(amp|lt|gt|quot|#39);/g, (m) => entities[m])
    .replace(/\s+/g, ' ')
    .trim()
}

const parsed = new Map<string, ParsedDoc>()

/** A page's headings and sections (kept: the texts never change while the page is open). */
export function parseDoc(text: string): ParsedDoc {
  const known = parsed.get(text)
  if (known) return known
  const slugger = new Slugger()
  const headings: Heading[] = []
  const sections: Section[] = []
  let body: string[] = []
  let current: Omit<Section, 'text'> | null = null
  const close = () => {
    if (current) sections.push({ ...current, text: plainText(body.join('')) })
    body = []
  }
  for (const t of lexer(text, { gfm: true })) {
    if (t.type === 'heading') {
      const h = t as Tokens.Heading
      const title = inlineText(h.tokens).trim()
      const id = slugger.slug(title)
      headings.push({ level: h.depth, text: title, id })
      if (h.depth <= 3) {
        close()
        current = { id, title, level: h.depth }
        continue
      }
    }
    body.push(t.raw)
  }
  close()
  const result = { headings, sections }
  parsed.set(text, result)
  return result
}

/** Where a link in a page of the manual goes. */
export type LinkTarget =
  /** A page of the manual (or a place on it): its address in the app. */
  | { kind: 'manual'; to: string; doc: ManualDoc | null }
  /** Another site: opened as it is. */
  | { kind: 'web' }
  /** A file of the repository the manual does not hold. */
  | { kind: 'source'; path: string }

/** a/b/../c to a/c; a path that climbs above the start keeps its leading ../ */
function normalize(path: string): string {
  const out: string[] = []
  for (const part of path.split('/')) {
    if (part === '' || part === '.') continue
    if (part === '..' && out.length && out[out.length - 1] !== '..') out.pop()
    else out.push(part)
  }
  return out.join('/')
}

const byFile = new Map(manualDocs.map((d) => [d.file, d]))

/** What a link in the page `from` (its file under docs/) points to. */
export function resolveLink(from: string, href: string): LinkTarget {
  if (/^[a-z][a-z0-9+.-]*:/i.test(href) || href.startsWith('//')) return { kind: 'web' }
  if (href === '/help' || href.startsWith('/help/') || href.startsWith('/help#')) {
    const id = href.slice('/help/'.length).split('#')[0]
    return { kind: 'manual', to: href, doc: manualDocs.find((d) => d.id === id) ?? null }
  }
  const [path, anchor] = href.split('#', 2) as [string, string | undefined]
  const hash = anchor ? `#${anchor}` : ''
  if (!path) {
    const self = byFile.get(from) ?? null
    return { kind: 'manual', to: `/help/${self?.id ?? ''}${hash}`, doc: self }
  }
  const dir = from.includes('/') ? from.slice(0, from.lastIndexOf('/') + 1) : ''
  const file = normalize(path.startsWith('/') ? path.slice(1) : dir + path)
  if (file === 'README.md') return { kind: 'manual', to: '/help', doc: null }
  const target = byFile.get(file)
  if (target) return { kind: 'manual', to: `/help/${target.id}${hash}`, doc: target }
  return { kind: 'source', path: file.startsWith('../') ? file.replace(/^(\.\.\/)+/, '') : `docs/${file}` }
}

export interface SearchHit {
  doc: { id: string; title: string }
  section: Section
  score: number
  /** A piece of the section around the first match, and where the words are in it. */
  snippet: string
  marks: [number, number][]
}

/** The words of a query, lower case, longest first (so "api key" marks "api" inside "apis" once). */
function words(query: string): string[] {
  return [...new Set(query.toLowerCase().split(/\s+/).filter(Boolean))].sort((a, b) => b.length - a.length)
}

function count(haystack: string, needle: string): number {
  let n = 0
  for (let at = haystack.indexOf(needle); at !== -1 && n < 20; at = haystack.indexOf(needle, at + needle.length)) n++
  return n
}

function snippetOf(text: string, terms: string[], phrase: string): Pick<SearchHit, 'snippet' | 'marks'> {
  const lower = text.toLowerCase()
  const first = [phrase, ...terms].map((w) => lower.indexOf(w)).find((i) => i >= 0) ?? 0
  let start = Math.max(0, first - 70)
  let end = Math.min(text.length, first + 150)
  if (start > 0) start = text.indexOf(' ', start) + 1 || start
  if (end < text.length) end = text.lastIndexOf(' ', end) > first ? text.lastIndexOf(' ', end) : end
  const snippet = `${start > 0 ? '…' : ''}${text.slice(start, end)}${end < text.length ? '…' : ''}`
  const lowerSnippet = snippet.toLowerCase()
  const marks: [number, number][] = []
  for (const w of terms)
    for (let at = lowerSnippet.indexOf(w); at !== -1; at = lowerSnippet.indexOf(w, at + w.length))
      if (!marks.some(([a, b]) => at < b && at + w.length > a)) marks.push([at, at + w.length])
  return { snippet, marks: marks.sort((a, b) => a[0] - b[0]) }
}

/**
 * Sections that have every word of the query, best first: a word in a section's title counts
 * most, then the whole query as written, then how often the words come.
 */
export function searchManual(pages: { id: string; title: string; text: string }[], query: string, limit = 40): SearchHit[] {
  const terms = words(query)
  if (!terms.length) return []
  const phrase = query.trim().toLowerCase().replace(/\s+/g, ' ')
  const hits: SearchHit[] = []
  for (const page of pages)
    for (const section of parseDoc(page.text).sections) {
      const title = section.title.toLowerCase()
      const text = section.text.toLowerCase()
      if (!terms.every((w) => title.includes(w) || text.includes(w) || page.title.toLowerCase().includes(w))) continue
      let score = 0
      for (const w of terms) score += (title.includes(w) ? 10 : 0) + Math.min(count(text, w), 10)
      if (terms.length > 1 && title.includes(phrase)) score += 20
      if (terms.length > 1 && text.includes(phrase)) score += 8
      if (section.level <= 2) score += 1
      hits.push({ doc: { id: page.id, title: page.title }, section, score, ...snippetOf(section.text, terms, phrase) })
    }
  return hits.sort((a, b) => b.score - a.score).slice(0, limit)
}
