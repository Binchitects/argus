import { slugify } from './slug'
import { topics, type HelpTopic, type ManualLink, type TopicId } from './topics'

/** The manual's page of page help, in the sidebar's order. */
const order: { title: string; ids: TopicId[] }[] = [
  { title: 'Everywhere', ids: ['app', 'manual'] },
  { title: 'Workspace', ids: ['home', 'chat', 'assistants', 'assistant', 'shared', 'prompts', 'tasks', 'usage', 'leaderboard', 'setup', 'account', 'ask'] },
  { title: 'Administration', ids: ['overview', 'people', 'person', 'groups', 'group', 'sign-in', 'models', 'tools', 'plugins', 'knowledge', 'storage', 'settings', 'audit', 'quality'] },
  { title: 'Argus', ids: ['indexing', 'packs', 'explore'] },
  { title: 'Observe', ids: ['monitoring', 'dashboards', 'dashboard', 'logs', 'alerts', 'traces'] },
  { title: 'Other pages', ids: ['login', 'design'] },
]

export const pagesTitle = 'Every page, explained'

/** Where a topic is on the manual's page of page help. */
export function topicAnchor(id: TopicId): string {
  return slugify(topics[id].title)
}

/** The manual's address of a link: /help/chat#assistants. */
export function manualHref(link: ManualLink): string {
  return `/help/${link.doc}${link.section ? `#${link.section}` : ''}`
}

/** Help text as Markdown: its **bold** stays bold, a lone * (release/*) stays a star. */
const md = (text: string) => text.replace(/(?<!\*)\*(?!\*)/g, '\\*')

function topicMarkdown(t: HelpTopic): string {
  const out = [`### ${t.title}`, '', md(t.about), '']
  if (t.parts.length) out.push('**On this page**', '', ...t.parts.map((p) => `- **${md(p.name)}**: ${md(p.text)}`), '')
  if (t.sections) out.push(`**${t.sectionNames?.all ?? 'Its parts'}**`, '', ...Object.values(t.sections).map((p) => `- **${md(p.name)}**: ${md(p.text)}`), '')
  for (const task of t.tasks) out.push(`**${md(task.title)}**`, '', ...task.steps.map((s, i) => `${i + 1}. ${md(s)}`), '')
  if (t.manual) out.push(`[Read more in the manual](${manualHref(t.manual)})`, '')
  return out.join('\n')
}

/** Every page's help as one page of the manual (an admin's has the admin pages too). */
export function pagesMarkdown(isAdmin: boolean): string {
  const out = [
    `# ${pagesTitle}`,
    '',
    'What each page of the app is for, what its parts do, and the common tasks, step by step. The **?** at the top of every page opens its part of this.',
    '',
  ]
  for (const group of order) {
    const shown = group.ids.filter((id) => isAdmin || !(topics[id] as HelpTopic).admin)
    if (!shown.length) continue
    out.push(`## ${group.title}`, '', ...shown.map((id) => topicMarkdown(topics[id])))
  }
  return out.join('\n')
}

/** Every topic the page of page help lists (route-help.test.ts: none is left out). */
export const pagesOrder: TopicId[] = order.flatMap((g) => g.ids)
