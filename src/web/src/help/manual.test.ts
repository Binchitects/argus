import { readdirSync, readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { lexer, type Token } from 'marked'
import { describe, expect, it } from 'vitest'
import { canRead, manualDocs, notInTheManual, parseDoc, plainText, resolveLink, searchManual } from './manual'
import { Slugger, slugify } from './slug'

// The tests run in src/web (its vite.config.ts).
const docsDir = resolve(process.cwd(), '../../docs') + '/'

/** Every link a page has, wherever it is (lists, tables, quotes). */
function links(text: string): string[] {
  const out: string[] = []
  const walk = (tokens: Token[] | undefined) => {
    for (const t of tokens ?? []) {
      if (t.type === 'link') out.push((t as { href: string }).href)
      if ('tokens' in t) walk(t.tokens)
      if ('items' in t) walk(t.items as Token[])
      if (t.type === 'table') {
        const table = t as unknown as { header: { tokens: Token[] }[]; rows: { tokens: Token[] }[][] }
        for (const cell of [...table.header, ...table.rows.flat()]) walk(cell.tokens)
      }
    }
  }
  walk(lexer(text, { gfm: true }))
  return out
}

describe('the manual', () => {
  it('holds every page of docs/ that people and admins need, and says which it leaves out', () => {
    const files = [...readdirSync(docsDir).filter((f) => f.endsWith('.md')), ...readdirSync(`${docsDir}argus`).filter((f) => f.endsWith('.md')).map((f) => `argus/${f}`)]
    const placed = new Set([...manualDocs.map((d) => d.file), ...notInTheManual])
    expect(files.filter((f) => !placed.has(f)), 'a new page under docs/: add it to the manual or to notInTheManual').toEqual([])
    expect(manualDocs.map((d) => d.file).filter((f) => notInTheManual.includes(f))).toEqual([])
  })

  it('is built from the pages as they are in docs/', () => {
    for (const d of manualDocs) expect(d.text, d.file).toBe(readFileSync(`${docsDir}${d.file}`, 'utf8'))
  })

  it('keeps the admins\' pages for admins', () => {
    const admin = manualDocs.find((d) => d.file === 'admin.md')!
    const chat = manualDocs.find((d) => d.file === 'chat.md')!
    expect(canRead(admin, false)).toBe(false)
    expect(canRead(admin, true)).toBe(true)
    expect(canRead(chat, false)).toBe(true)
  })

  it.each(manualDocs.map((d) => [d.file, d] as const))('%s: every link to another page lands on a heading it has', (_, doc) => {
    for (const href of links(doc.text)) {
      const target = resolveLink(doc.file, href)
      if (target.kind !== 'manual' || !target.doc) continue
      const anchor = href.split('#')[1]
      if (anchor) expect(parseDoc(target.doc.text).headings.map((h) => h.id), `${doc.file}: ${href}`).toContain(anchor)
    }
  })

  it('points links into the app, the web as it is, and files it does not hold at the source', () => {
    expect(resolveLink('chat.md', 'admin.md#models')).toMatchObject({ kind: 'manual', to: '/help/admin#models' })
    expect(resolveLink('chat.md', '#memory')).toMatchObject({ kind: 'manual', to: '/help/chat#memory' })
    expect(resolveLink('argus/clients.md', '../chat.md')).toMatchObject({ kind: 'manual', to: '/help/chat' })
    expect(resolveLink('chat.md', 'argus/README.md')).toMatchObject({ kind: 'manual', to: '/help/argus' })
    expect(resolveLink('argus/overview.md', 'clients.md#stdio')).toMatchObject({ kind: 'manual', to: '/help/argus/clients#stdio' })
    expect(resolveLink('chat.md', 'README.md')).toEqual({ kind: 'manual', to: '/help', doc: null })
    expect(resolveLink('pages.md', '/help/settings#secrets')).toMatchObject({ kind: 'manual', to: '/help/settings#secrets' })
    expect(resolveLink('settings.md', '../LICENSING.md')).toEqual({ kind: 'source', path: 'LICENSING.md' })
    expect(resolveLink('argus/overview.md', 'standalone/configuration.md')).toEqual({ kind: 'source', path: 'docs/argus/standalone/configuration.md' })
    expect(resolveLink('chat.md', 'plan.md')).toEqual({ kind: 'source', path: 'docs/plan.md' })
    expect(resolveLink('chat.md', 'https://example.com/a.md')).toEqual({ kind: 'web' })
    expect(resolveLink('chat.md', 'mailto:help@example.com')).toEqual({ kind: 'web' })
  })

  it('makes anchors as GitHub does', () => {
    expect(slugify('Usage & cost')).toBe('usage--cost')
    expect(slugify('Decide (Laya)')).toBe('decide-laya')
    expect(slugify('The company directory (LDAP / Active Directory)')).toBe('the-company-directory-ldap--active-directory')
    const s = new Slugger()
    expect([s.slug('Settings'), s.slug('Settings'), s.slug('Settings-1'), s.slug('Settings')]).toEqual(['settings', 'settings-1', 'settings-1-1', 'settings-2'])
  })

  it('cuts a page into sections at its headings, never at a # inside code', () => {
    const page = parseDoc('# Title\n\nIntro.\n\n## `.env` and you\n\nText with [a link](x.md).\n\n```sh\n# not a heading\n```\n\n### Deeper\n\nMore.\n\n#### Deepest\n\nStays in Deeper.\n')
    expect(page.headings.map((h) => [h.level, h.id])).toEqual([
      [1, 'title'],
      [2, 'env-and-you'],
      [3, 'deeper'],
      [4, 'deepest'],
    ])
    expect(page.sections.map((s) => s.id)).toEqual(['title', 'env-and-you', 'deeper'])
    expect(page.sections[1].text).toContain('Text with a link.')
    expect(page.sections[1].text).toContain('not a heading')
    expect(page.sections[2].text).toContain('Stays in Deeper.')
    expect(plainText('**Bold** and `code` | cell |')).toBe('Bold and code cell')
    expect(plainText('| a | b |\n|---|:--:|\n| c | d |\n\n---\n\nAfter')).toBe('a b c d After')
  })

  it('searches every section for all the words, best first, and marks them', () => {
    const pages = [
      { id: 'a', title: 'A', text: '# A\n\n## API keys\n\nEach person has an API key.\n\n## Credit\n\nThe key spends from your credit.\n' },
      { id: 'b', title: 'B', text: '# B\n\n## Keys and locks\n\nAn api to open locks with a key.\n' },
    ]
    const hits = searchManual(pages, 'API key')
    expect(hits.map((h) => `${h.doc.id}#${h.section.id}`)).toEqual(['a#api-keys', 'b#keys-and-locks'])
    const first = hits[0]
    expect(first.marks.map(([a, b]) => first.snippet.slice(a, b).toLowerCase())).toEqual(['api', 'key'])
    expect(searchManual(pages, 'credit key').map((h) => h.section.id)).toEqual(['credit'])
    expect(searchManual(pages, 'nothing like this')).toEqual([])
    expect(searchManual(pages, '   ')).toEqual([])
  })

  it('finds what people ask about in the real pages', () => {
    const hits = searchManual(manualDocs, 'legal hold')
    expect(hits[0].doc.id).toBe('admin')
    expect(hits[0].section.id).toBe('retention-legal-hold-and-exports')
    expect(searchManual(manualDocs.filter((d) => canRead(d, false)), 'legal hold').every((h) => h.doc.id !== 'admin')).toBe(true)
  })
})
