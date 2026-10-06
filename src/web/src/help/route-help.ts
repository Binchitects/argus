import { matchPath } from 'react-router'
import { topics, type HelpPart, type HelpTopic, type TopicId } from './topics'

/**
 * Every route of app/routes.tsx, by its whole path, and the help for it. A route with
 * no help here fails route-help.test.ts: a new page adds its line here and its topic.
 */
export const routeHelp: Record<string, TopicId> = {
  '/login': 'login',
  '/': 'home',
  '/account': 'account',
  '/ask': 'ask',
  '/design': 'design',
  '/chat': 'chat',
  '/chat/:id': 'chat',
  '/chat/assistants/:assistantId': 'assistant',
  '/shared/:shareId': 'shared',
  '/assistants': 'assistants',
  '/prompts': 'prompts',
  '/tasks': 'tasks',
  '/usage': 'usage',
  '/leaderboard': 'leaderboard',
  '/setup': 'setup',
  '/help/*': 'manual',
  '/admin': 'overview',
  '/admin/people': 'people',
  '/admin/people/:id': 'person',
  '/admin/groups': 'groups',
  '/admin/groups/:id': 'group',
  '/admin/tools': 'tools',
  '/admin/plugins': 'plugins',
  '/admin/knowledge': 'knowledge',
  '/admin/sign-in': 'sign-in',
  '/admin/models': 'models',
  '/admin/settings': 'settings',
  '/admin/audit': 'audit',
  '/admin/quality': 'quality',
  '/admin/indexing': 'indexing',
  '/admin/packs': 'packs',
  '/admin/explore': 'explore',
  '/admin/monitoring': 'monitoring',
  '/admin/dashboards': 'dashboards',
  '/admin/dashboards/:uid': 'dashboard',
  '/admin/logs': 'logs',
  '/admin/alerts': 'alerts',
  '/admin/traces': 'traces',
}

/** How specific a route is: its fixed segments count most, then all of them. */
function rank(pattern: string): number {
  const segments = pattern.split('/').filter(Boolean)
  return segments.filter((s) => !s.startsWith(':') && s !== '*').length * 100 + segments.length
}

export interface FoundHelp {
  id: TopicId
  topic: HelpTopic
  /** The part of the page the address points at (a Settings group, a dashboard), when it has its own help. */
  section: (HelpPart & { id: string }) | null
}

/**
 * The help for an address: the most specific route that matches it. An admin page's help is
 * for admins; anyone else, and an address no route knows, gets the help around every page.
 */
export function helpFor(pathname: string, hash: string, isAdmin: boolean): FoundHelp {
  const pattern = Object.keys(routeHelp)
    .filter((p) => matchPath(p, pathname))
    .sort((a, b) => rank(b) - rank(a))[0]
  let id: TopicId = pattern ? routeHelp[pattern] : 'app'
  if ((topics[id] as HelpTopic).admin && !isAdmin) id = 'app'
  const topic: HelpTopic = topics[id]
  let anchor = hash.replace(/^#/, '')
  try {
    anchor = decodeURIComponent(anchor)
  } catch {
    // a malformed address: its anchor as written
  }
  const last = pathname.split('/').filter(Boolean).pop() ?? ''
  const key = [anchor, last].find((k) => k && topic.sections && Object.hasOwn(topic.sections, k))
  return { id, topic, section: key ? { ...topic.sections![key], id: key } : null }
}
