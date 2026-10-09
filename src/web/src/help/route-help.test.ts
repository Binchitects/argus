import { readdirSync, readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import type { RouteObject } from 'react-router'
import { describe, expect, it } from 'vitest'
import { routes } from '@/app/routes'
import { slug as settingsSlug } from '@/pages/admin/settings-model'
import { canRead, manualDocs, parseDoc } from './manual'
import { pagesMarkdown, pagesOrder, topicAnchor } from './pages-doc'
import { helpFor, routeHelp } from './route-help'
import { topicList, topics, type HelpTopic, type TopicId } from './topics'

/** Every page's whole path in app/routes.tsx (not the "*" of a page not found). */
function pagePaths(list: RouteObject[], parent = ''): string[] {
  return list.flatMap((r) => {
    if (r.path === '*') return []
    const here = r.path === undefined ? parent : r.path.startsWith('/') ? r.path : `${parent.replace(/\/$/, '')}/${r.path}`
    const own = r.index ? [parent || '/'] : r.path !== undefined && !r.children ? [here] : []
    return [...own, ...(r.children ? pagePaths(r.children, here) : [])]
  })
}

const paths = [...new Set(pagePaths(routes))]
const example = (path: string) => path.replace(/:[^/]+/g, 'x1').replace('*', 'chat')

describe('help for every page', () => {
  it('finds the pages of the app', () => {
    expect(paths).toContain('/')
    expect(paths).toContain('/admin')
    expect(paths).toContain('/admin/settings')
    expect(paths).toContain('/chat/assistants/:assistantId')
    expect(paths).toContain('/help/*')
    expect(paths.length).toBeGreaterThan(35)
  })

  it.each(paths)('%s has its help', (path) => {
    const id = routeHelp[path]
    expect(id, `${path} has no line in help/route-help.ts`).toBeDefined()
    expect(topics[id]).toBeDefined()
    // The address finds that help, not a neighbour's (/chat/assistants/x is not /chat/:id).
    expect(helpFor(example(path), '', true).id).toBe(id)
  })

  it('lists no route the app does not have', () => {
    expect(Object.keys(routeHelp).filter((p) => !paths.includes(p))).toEqual([])
  })

  it('keeps the admin pages to admins, and has help for an address no page has', () => {
    expect(helpFor('/admin/people', '', true).topic.title).toBe('People')
    expect(helpFor('/admin/people', '', false).id).toBe('app')
    expect(helpFor('/no/such/page', '', false).id).toBe('app')
    expect(helpFor('/chat/123', '', false).id).toBe('chat')
  })

  it('has help for every group of Settings, by its anchor', () => {
    const catalog = readFileSync(resolve(process.cwd(), '../Llm.Api/Settings/SettingsCatalog.cs'), 'utf8')
    const groups = [...catalog.matchAll(/private const string \w+ = "([^"]+)";/g)].map((m) => m[1]).filter((g) => !/[:=]/.test(g))
    expect(groups).toContain('Company directory (LDAP)')
    const sections = Object.keys(topics.settings.sections)
    expect(groups.map(settingsSlug).filter((s) => !sections.includes(s))).toEqual([])
    expect(sections.filter((s) => !groups.map(settingsSlug).includes(s))).toEqual([])
    const found = helpFor('/admin/settings', '#company-directory-ldap', true)
    expect(found.section?.name).toBe('Company directory (LDAP)')
    expect(helpFor('/admin/settings', '#%E0', true).section).toBeNull()
  })

  it('names the company directory\'s buttons and checks as its pages show them', () => {
    const pages = ['pages/admin/directory-panel.tsx', 'pages/admin/sign-in.tsx', 'app/nav.ts'].map((f) => readFileSync(resolve(process.cwd(), 'src', f), 'utf8')).join('\n')
    const help = [topics.settings.sections['company-directory-ldap'].text, ...topics['sign-in'].parts.map((p) => p.text), ...topics['sign-in'].tasks.flatMap((t) => t.steps)]
    const named = help.flatMap((t) => [...t.matchAll(/\*\*(.+?)\*\*/g)].map((m) => m[1]))
    expect(named).toEqual(expect.arrayContaining(['Test the settings', "Try a person's sign-in", 'Try it']))
    expect(named.filter((n) => !pages.includes(n))).toEqual([])
  })

  it('has help for every dashboard, by its address', () => {
    const dir = resolve(process.cwd(), '../Llm.Api/Dashboards/json')
    const uids = readdirSync(dir).map((f) => (JSON.parse(readFileSync(`${dir}/${f}`, 'utf8')) as { uid: string }).uid)
    expect(uids).toContain('gpu-hardware')
    const sections = Object.keys(topics.dashboard.sections)
    expect(uids.filter((u) => !sections.includes(u))).toEqual([])
    expect(sections.filter((u) => !uids.includes(u))).toEqual([])
    expect(helpFor('/admin/dashboards/gpu-hardware', '', true).section?.name).toBe('GPU Hardware')
    expect(helpFor('/admin/dashboards', '', true).section).toBeNull()
  })

  it.each(topicList)('%s says what the page is for, its parts, and links to a section that exists', (_, topic: HelpTopic) => {
    expect(topic.about.length).toBeGreaterThan(20)
    expect(topic.parts.length).toBeGreaterThan(0)
    for (const t of topic.tasks) expect(t.steps.length).toBeGreaterThan(0)
    if (!topic.manual) return
    const doc = manualDocs.find((d) => d.id === topic.manual!.doc)
    expect(doc, `no page ${topic.manual.doc} in the manual`).toBeDefined()
    // Help everyone reads links to a page everyone may read.
    if (!topic.admin) expect(canRead(doc!, false)).toBe(true)
    if (topic.manual.section) expect(parseDoc(doc!.text).headings.map((h) => h.id)).toContain(topic.manual.section)
  })

  it('puts every page\'s help on the manual\'s page of page help, once, at its anchor', () => {
    expect([...pagesOrder].sort()).toEqual(topicList.map(([id]) => id).sort())
    const adminIds = parseDoc(pagesMarkdown(true)).headings.map((h) => h.id)
    for (const [id] of topicList) expect(adminIds).toContain(topicAnchor(id))
    const memberIds = parseDoc(pagesMarkdown(false)).headings.map((h) => h.id)
    const adminOnly = topicList.filter(([, t]) => t.admin).map(([id]) => id as TopicId)
    expect(adminOnly.length).toBeGreaterThan(10)
    for (const id of adminOnly) expect(memberIds).not.toContain(topicAnchor(id))
    expect(memberIds).toContain(topicAnchor('chat'))
  })
})
