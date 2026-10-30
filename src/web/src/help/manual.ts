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
import compliance from '@docs/compliance/README.md?raw'
import complianceControls from '@docs/compliance/controls.md?raw'
import complianceFlows from '@docs/compliance/data-flows.md?raw'
import complianceGdpr from '@docs/compliance/frameworks/gdpr.md?raw'
import complianceHipaa from '@docs/compliance/frameworks/hipaa.md?raw'
import complianceIso from '@docs/compliance/frameworks/iso27001.md?raw'
import complianceSoc2 from '@docs/compliance/frameworks/soc2.md?raw'
import complianceIso42001 from '@docs/compliance/frameworks/iso42001.md?raw'
import complianceIso22301 from '@docs/compliance/frameworks/iso22301.md?raw'
import compliancePci from '@docs/compliance/frameworks/pcidss.md?raw'
import complianceCcm from '@docs/compliance/frameworks/csaccm.md?raw'
import complianceCsf from '@docs/compliance/frameworks/nistcsf.md?raw'
import compliance80053 from '@docs/compliance/frameworks/nist80053.md?raw'
import compliance800171 from '@docs/compliance/frameworks/nist800171.md?raw'
import complianceAiRmf from '@docs/compliance/frameworks/airmf.md?raw'
import complianceAiAct from '@docs/compliance/frameworks/euaiact.md?raw'
import complianceCc from '@docs/compliance/frameworks/commoncriteria.md?raw'
import complianceIsms from '@docs/compliance/isms.md?raw'
import complianceEvaluation from '@docs/compliance/product-evaluation.md?raw'
import policyAi from '@docs/compliance/policies/ai.md?raw'
import policyLogging from '@docs/compliance/policies/logging-monitoring.md?raw'
import complianceIncidents from '@docs/compliance/incident-response.md?raw'
import policyUse from '@docs/compliance/policies/acceptable-use.md?raw'
import policyAccess from '@docs/compliance/policies/access-control.md?raw'
import policyContinuity from '@docs/compliance/policies/business-continuity.md?raw'
import policyChange from '@docs/compliance/policies/change-management.md?raw'
import policyCrypto from '@docs/compliance/policies/cryptography.md?raw'
import policyData from '@docs/compliance/policies/data-protection.md?raw'
import policySecurity from '@docs/compliance/policies/information-security.md?raw'
import policyPeople from '@docs/compliance/policies/people-security.md?raw'
import policyPhysical from '@docs/compliance/policies/physical-security.md?raw'
import policyRisk from '@docs/compliance/policies/risk-management.md?raw'
import policySuppliers from '@docs/compliance/policies/suppliers.md?raw'
import complianceDpia from '@docs/compliance/templates/dpia.md?raw'
import complianceRopa from '@docs/compliance/templates/record-of-processing.md?raw'
import configuration from '@docs/configuration.md?raw'
import cpuTemperature from '@docs/cpu-temperature.md?raw'
import data from '@docs/data.md?raw'
import decks from '@docs/decks.md?raw'
import deployment from '@docs/deployment.md?raw'
import scripts from '@docs/scripts.md?raw'
import multiGpu from '@docs/multi-gpu-server.md?raw'
import hermes from '@docs/hermes.md?raw'
import integrations from '@docs/integrations.md?raw'
import knowledge from '@docs/knowledge.md?raw'
import mcp from '@docs/mcp.md?raw'
import media from '@docs/media.md?raw'
import plugins from '@docs/plugins.md?raw'
import settings from '@docs/settings.md?raw'
import studio from '@docs/studio.md?raw'
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
      doc('data.md', 'Ask your data', 'The company\'s databases asked in words: the SQL shown and explained, charts, boards; connecting PostgreSQL, MySQL or SQL Server read only.', 'everyone', data),
      doc('media.md', 'Media studio', 'Pictures from words, changed where painted, varied, enlarged and set moving; vector drawings that move.', 'everyone', media),
      doc('decks.md', 'Decks', 'Slides from a few words, changed by asking, presented, downloaded as PowerPoint or PDF; the company\'s template.', 'everyone', decks),
      doc('studio.md', 'Agent Studio', 'Workflows that do the work as you: triggers, steps, approvals, runs to replay, forms to share.', 'everyone', studio),
      doc('integrations.md', 'Bots, email and the app', 'Slack, Mattermost and Teams bots, email in, the installed app with push notifications, the browser extension.', 'everyone', integrations),
      doc('mcp.md', 'Arena MCP', 'Your chat tools for your own agent, at /mcp (or as code at /mcp/code), signed in with your API key.', 'everyone', mcp),
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
      doc('compliance/README.md', 'Compliance', 'Fourteen frameworks, from ISO 27001 and SOC 2 to the EU AI Act: what Arena covers and the company does, the controls, and the checks that keep them.', 'admins', compliance),
      doc('knowledge.md', 'Company knowledge', 'GitLab, GitHub, Confluence, Jira, Notion, SharePoint, WebDAV, S3, folders, websites, feeds and uploads, searched by each person within their rights.', 'admins', knowledge),
      doc('plugins.md', 'Plugins and APIs', 'Installing plugins, each person\'s own account, the manifest, a catalog.', 'admins', plugins),
      doc('deployment.md', 'Deploying', 'The samples and secrets, addresses, a host with no network, switching the model, the traps.', 'admins', deployment),
      doc('scripts.md', 'Scripts and arenactl', 'The one command: checking a machine, installing from a checkout or the offline installer, Code Arena\'s packages, the checks.', 'admins', scripts),
      doc('multi-gpu-server.md', 'A multi-GPU server', 'Large models on several GPUs: what fits, vLLM and SGLang beside llama.cpp, the KV cache tiers, durability, what to buy.', 'admins', multiGpu),
      doc('configuration.md', 'Configuration', 'Every .env variable and every file under deploy/config/.', 'admins', configuration),
      doc('architecture.md', 'Architecture', 'Every service, how a request flows through them, where state lives, what each failure looks like.', 'admins', architecture),
      doc('cpu-temperature.md', 'CPU temperature', 'How CPU temperature reaches the dashboards, on Linux and on Windows.', 'admins', cpuTemperature),
    ],
  },
  {
    title: 'Compliance documents',
    docs: [
      doc('compliance/controls.md', 'The controls', 'Every control: what meets it, the clauses it serves, the tests and checks behind it, the company\'s part.', 'admins', complianceControls),
      doc('compliance/isms.md', 'The management system', 'ISO 27001, 42001 and 22301\'s clauses 4 to 10: scope, roles, objectives, audits, reviews.', 'admins', complianceIsms),
      doc('compliance/frameworks/iso27001.md', 'ISO 27001, clause by clause', 'Clauses 4 to 10 and Annex A\'s 93 controls, Arena\'s controls and the company\'s document for each.', 'admins', complianceIso),
      doc('compliance/frameworks/soc2.md', 'SOC 2, criterion by criterion', 'The Trust Services Criteria, Arena\'s controls and the company\'s document for each.', 'admins', complianceSoc2),
      doc('compliance/frameworks/hipaa.md', 'HIPAA, section by section', 'The Security Rule, breach notification and the Privacy Rule\'s parts, with Arena\'s controls.', 'admins', complianceHipaa),
      doc('compliance/frameworks/gdpr.md', 'The GDPR, article by article', 'The articles a controller\'s systems meet, with Arena\'s controls.', 'admins', complianceGdpr),
      doc('compliance/frameworks/nistcsf.md', 'NIST CSF 2.0', 'The Cybersecurity Framework\'s categories and subcategories, with Arena\'s controls.', 'admins', complianceCsf),
      doc('compliance/frameworks/nist80053.md', 'NIST SP 800-53', 'Every control and enhancement of Rev. 5, with the baselines FedRAMP builds on.', 'admins', compliance80053),
      doc('compliance/frameworks/nist800171.md', 'NIST SP 800-171', 'Rev. 3\'s requirements for controlled unclassified information (CMMC).', 'admins', compliance800171),
      doc('compliance/frameworks/pcidss.md', 'PCI DSS', 'The twelve requirements and their sections, with Arena\'s controls.', 'admins', compliancePci),
      doc('compliance/frameworks/csaccm.md', 'CSA STAR (CCM)', 'The Cloud Controls Matrix\'s domains, with Arena\'s controls.', 'admins', complianceCcm),
      doc('compliance/frameworks/iso42001.md', 'ISO 42001, clause by clause', 'The AI management system: clauses 4 to 10 and Annex A\'s 38 controls.', 'admins', complianceIso42001),
      doc('compliance/frameworks/euaiact.md', 'The EU AI Act, article by article', 'Regulation (EU) 2024/1689\'s articles and annexes, with Arena\'s controls.', 'admins', complianceAiAct),
      doc('compliance/frameworks/airmf.md', 'NIST AI RMF', 'Govern, Map, Measure and Manage, subcategory by subcategory.', 'admins', complianceAiRmf),
      doc('compliance/frameworks/iso22301.md', 'ISO 22301, clause by clause', 'The business continuity management system, with Arena\'s controls.', 'admins', complianceIso22301),
      doc('compliance/frameworks/commoncriteria.md', 'Common Criteria', 'The functional and assurance families a product evaluation reads.', 'admins', complianceCc),
      doc('compliance/product-evaluation.md', 'Product evaluation', 'What a Common Criteria evaluation of Arena rests on, and FIPS 140-3.', 'admins', complianceEvaluation),
      doc('compliance/data-flows.md', 'Data flows', 'Where people\'s data goes in the stack, and the flows out of the company an admin turns on.', 'admins', complianceFlows),
      doc('compliance/incident-response.md', 'Incident response', 'From noticing to closure, with the register\'s steps and the law\'s clocks.', 'admins', complianceIncidents),
      doc('compliance/templates/record-of-processing.md', 'Record of processing', 'GDPR Art. 30, filled in with what Arena processes.', 'admins', complianceRopa),
      doc('compliance/templates/dpia.md', 'Impact assessment (DPIA)', 'GDPR Art. 35, with Arena\'s measures for each risk.', 'admins', complianceDpia),
      doc('compliance/policies/information-security.md', 'Information security policy', 'A template: roles, the rules, records, exceptions.', 'admins', policySecurity),
      doc('compliance/policies/risk-management.md', 'Risk management policy', 'A template: the asset inventory and the risk register, started for you.', 'admins', policyRisk),
      doc('compliance/policies/access-control.md', 'Access control policy', 'A template: accounts, two-factor, least privilege, reviews, leavers.', 'admins', policyAccess),
      doc('compliance/policies/acceptable-use.md', 'Acceptable use policy', 'A template: the notice everyone accepts at sign-in, in full.', 'admins', policyUse),
      doc('compliance/policies/people-security.md', 'People security policy', 'A template: screening, training, leaving.', 'admins', policyPeople),
      doc('compliance/policies/physical-security.md', 'Physical security policy', 'A template: where the hosts are, the equipment, media and disposal.', 'admins', policyPhysical),
      doc('compliance/policies/business-continuity.md', 'Backup and continuity policy', 'A template: backups, restores proven, capacity, losing the host.', 'admins', policyContinuity),
      doc('compliance/policies/data-protection.md', 'Data protection policy', 'A template: classes, people\'s rights, how long things are kept.', 'admins', policyData),
      doc('compliance/policies/suppliers.md', 'Supplier and transfer policy', 'A template: cloud models and other suppliers, contracts, transfers.', 'admins', policySuppliers),
      doc('compliance/policies/change-management.md', 'Change and vulnerability policy', 'A template: upgrades, rollback, the SBOM, patching.', 'admins', policyChange),
      doc('compliance/policies/cryptography.md', 'Cryptography policy', 'A template: TLS, encryption at rest, the keys and who holds them.', 'admins', policyCrypto),
      doc('compliance/policies/logging-monitoring.md', 'Logging and monitoring policy', 'A template: what is recorded, kept and watched.', 'admins', policyLogging),
      doc('compliance/policies/ai.md', 'AI policy', 'A template: purposes, the AI Act\'s classes, oversight, evaluation, impact assessments.', 'admins', policyAi),
    ],
  },
]

/** Pages of docs/ kept out of the manual: the developers', and Argus run alone, without this platform. */
export const notInTheManual = [
  'README.md',
  'development.md',
  'testing.md',
  'plan.md',
  // Designs for the work under way, as plan.md is: not how the app works yet.
  'design/code-arena-parity.md',
  'design/code-index.md',
  'design/own-gateway.md',
  'design/v5.9-code-review-access.md',
  'design/v5.9-rooms.md',
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
