import { Activity, AppWindow, BookOpenText, FolderKanban, ListOrdered, Megaphone, MessageSquareHeart, BadgeCheck, AudioLines, Brain, GitPullRequest, ListChecks, ListTodo, BarChart, BarChart3, Blocks, Bot, Boxes, CalendarClock, Database, FlaskConical, HardDrive, Home, KeyRound, LayoutDashboard, Library, LineChart, Logs, MessageSquare, Package, Palette, Plug, Presentation, ScanSearch, ScrollText, Settings, ShieldAlert, ShieldCheck, Siren, SquareSlash, Telescope, ThumbsUp, Timer, Trophy, Users, UsersRound, Workflow, Wrench, type LucideIcon, Calculator, Gauge, Shield, Sparkles } from 'lucide-react'
import { codeIntelName } from '@/lib/brand'

export interface NavItem {
  title: string
  path: string
  icon: LucideIcon
  /** Words the command palette also matches. */
  keywords?: string[]
  /** In an admin section: the permission that shows it (an Owner holds every one). */
  permission?: string
  /** ... or any of these. */
  anyOf?: string[]
  /** The feature it is part of (deploy/features): left out while that feature is off. */
  feature?: string
  /** The feature permissions it needs, any of them (Admin → Access): left out for a person who holds none. */
  may?: string[]
}

export interface NavSection {
  title: string
  /** The sidebar's tab it is under: the tools and chats, the dashboards, or the administration. */
  space: Space
  adminOnly?: boolean
  items: NavItem[]
}

/** The sidebar's three tabs, each a sidebar of its own: what people use, what is happening, how it is run. */
export type Space = 'work' | 'monitor' | 'admin'
export const spaces: { id: Space; title: string; icon: LucideIcon }[] = [
  { id: 'work', title: 'Workspace', icon: Sparkles },
  { id: 'monitor', title: 'Monitoring', icon: Gauge },
  { id: 'admin', title: 'Admin', icon: Shield },
]
export const spaceTitle = (id: Space) => spaces.find((s) => s.id === id)!.title

/** Each tab's groups, the most used first. */
export const navigation: NavSection[] = [
  {
    title: 'Everyday',
    space: 'work',
    items: [
      { title: 'Home', path: '/', icon: Home },
      { title: 'Chat', path: '/chat', icon: MessageSquare, keywords: ['conversation', 'ask', 'model', 'code-intel'], may: ['feature.chat'] },
      { title: 'Assistants', path: '/assistants', icon: Bot, keywords: ['gpts', 'gems', 'projects', 'instructions', 'knowledge', 'shared', 'team'] },
      { title: 'Projects', path: '/projects', icon: FolderKanban, keywords: ['project', 'team', 'accounting', 'budget', 'progress', 'goals', 'milestones', 'tasks', 'report', 'export', 'pdf', 'excel', 'global'] },
      { title: 'Wiki', path: '/wiki', icon: BookOpenText, keywords: ['search', 'knowledge', 'documents', 'docs', 'handbook', 'code', 'files', 'tables', 'find', 'browse'], may: ['feature.wiki'] },
    ],
  },
  {
    title: 'Build',
    space: 'work',
    items: [
      { title: 'Code Studio', path: '/code', icon: GitPullRequest, keywords: ['ide', 'vs code', 'workspace', 'editor', 'terminal', 'debug', 'review', 'merge request', 'mr', 'gitlab', 'diff', 'code assistant', 'branch', 'code-intel', 'security', 'vulnerability', 'cwe', 'owasp'], feature: 'code-studio', may: ['code.studio', 'code.read', 'review.run'] },
      { title: 'Security review', path: '/security', icon: ShieldCheck, keywords: ['security', 'vulnerabilities', 'sast', 'cwe', 'injection', 'secrets', 'dependencies', 'cve', 'scanners', 'several repositories', 'cross-repo', 'audit', 'pentest'], feature: 'code-studio', may: ['review.run', 'code.read'] },
      { title: 'Agent Studio', path: '/studio', icon: Workflow, keywords: ['workflow', 'automation', 'agent', 'zapier', 'n8n', 'flow', 'trigger', 'approval', 'webhook', 'form'], feature: 'agents', may: ['feature.studio'] },
      { title: 'Scheduled tasks', path: '/tasks', icon: CalendarClock, keywords: ['schedule', 'cron', 'daily', 'weekly', 'report', 'digest', 'webhook', 'email', 'notification'], may: ['feature.tasks'] },
      { title: 'Apps', path: '/apps', icon: AppWindow, keywords: ['app', 'apps', 'tool', 'publish', 'page', 'component', 'gallery', 'remix'], feature: 'apps' },
    ],
  },
  {
    title: 'Create',
    space: 'work',
    items: [
      { title: 'Media studio', path: '/media', icon: Palette, keywords: ['pictures', 'images', 'photo', 'edit', 'inpaint', 'paint', 'variations', 'upscale', 'enlarge', 'animate', 'video', 'clip', 'midjourney', 'photoshop'], feature: 'media', may: ['feature.pictures'] },
      { title: 'Decks', path: '/decks', icon: Presentation, keywords: ['slides', 'presentation', 'powerpoint', 'pptx', 'pdf', 'deck', 'keynote', 'template'], feature: 'decks', may: ['feature.decks'] },
      { title: 'Data', path: '/data', icon: BarChart, keywords: ['ask your data', 'sql', 'database', 'query', 'chart', 'dashboard', 'board', 'report', 'analytics', 'bi'], feature: 'data', may: ['feature.data'] },
      { title: 'Meetings', path: '/meetings', icon: AudioLines, keywords: ['meeting', 'record', 'recording', 'transcript', 'minutes', 'notes', 'action items', 'decisions', 'who said', 'speakers', 'otter', 'fireflies', 'call'], feature: 'meetings', may: ['feature.meetings'] },
      { title: 'Science lab', path: '/lab', icon: FlaskConical, keywords: ['science', 'lab', 'mathematics', 'math', 'physics', 'chemistry', 'statistics', 'proof', 'sympy', 'units', 'notebook', 'wolfram'], feature: 'lab', may: ['feature.lab'] },
    ],
  },
  {
    title: 'Organize',
    space: 'work',
    items: [
      { title: 'Plans', path: '/plans', icon: ListChecks, keywords: ['plan', 'board', 'timeline', 'gantt', 'steps', 'project', 'kanban'], may: ['feature.plans'] },
      { title: 'Checklists', path: '/checklists', icon: ListTodo, keywords: ['checklist', 'definition of done', 'steps', 'review', 'before merge', 'tick', 'todo'] },
      { title: 'Skills', path: '/skills', icon: SquareSlash, keywords: ['slash commands', 'prompts', 'prompt library', 'templates', 'snippets', 'instructions', 'parameters'] },
      { title: 'Memory', path: '/memory', icon: Brain, keywords: ['remember', 'memories', 'recall', 'preferences', 'facts', 'decisions', 'lessons', 'pinned', 'context', 'what it knows about me'], may: ['feature.memory'] },
    ],
  },
  {
    title: 'You',
    space: 'work',
    items: [
      { title: 'Connect your tools', path: '/setup', icon: Plug, keywords: ['api key', 'token', 'claude code', 'qwen code', 'opencode', 'continue', 'mcp', 'harness', 'sdk', 'certificate'], may: ['feature.api_keys'] },
      { title: 'Usage & cost', path: '/usage', icon: BarChart3, keywords: ['tokens', 'spend', 'credit', 'budget'] },
      { title: 'Leaderboard', path: '/leaderboard', icon: Trophy, keywords: ['arena', 'compare', 'models', 'elo', 'vote', 'ranking'] },
    ],
  },
  {
    title: 'Live',
    space: 'monitor',
    adminOnly: true,
    items: [
      { title: 'Overview', path: '/admin', icon: LayoutDashboard, keywords: ['status', 'health', 'services'], permission: 'monitoring.read' },
      { title: 'Dashboards', path: '/admin/dashboards', icon: LineChart, keywords: ['gpu', 'performance', 'metrics', 'charts', 'hardware', 'system', 'users'], anyOf: ['usage.read', 'monitoring.read', 'hardware.read'], feature: 'monitoring' },
      { title: 'Line', path: '/admin/line', icon: ListOrdered, keywords: ['queue', 'waiting', 'busy', 'running', 'who waits', 'at once', 'remove', 'kick out', 'stop a request'], permission: 'usage.read' },
      { title: 'Monitoring', path: '/admin/monitoring', icon: Activity, keywords: ['prometheus', 'probes', 'health', 'services'], permission: 'monitoring.read' },
      { title: 'Alerts', path: '/admin/alerts', icon: Siren, keywords: ['alertmanager', 'firing', 'rules', 'incidents'], permission: 'monitoring.read' },
    ],
  },
  {
    title: 'Investigate',
    space: 'monitor',
    adminOnly: true,
    items: [
      { title: 'Logs', path: '/admin/logs', icon: Logs, keywords: ['loki', 'errors', 'containers', 'tail'], permission: 'logs.read', feature: 'monitoring' },
      { title: 'Traces', path: '/admin/traces', icon: Timer, keywords: ['slow', 'answers', 'latency', 'timeline', 'sub-agents', 'speed', 'tokens'], permission: 'traces.read' },
      { title: 'Quality', path: '/admin/quality', icon: ThumbsUp, keywords: ['feedback', 'thumbs', 'ratings', 'arena', 'leaderboard', 'answers'], permission: 'monitoring.read' },
    ],
  },
  {
    title: 'Cost',
    space: 'monitor',
    adminOnly: true,
    items: [
      { title: 'Accounting', path: '/admin/accounting', icon: Calculator, keywords: ['accountant', 'finance', 'cost', 'power', 'electricity', 'kwh', 'hardware', 'depreciation', 'chargeback', 'cost centre', 'budget', 'report'], permission: 'billing.read' },
    ],
  },
  {
    title: 'People',
    space: 'admin',
    adminOnly: true,
    items: [
      { title: 'People', path: '/admin/people', icon: Users, keywords: ['users', 'accounts', 'credit', 'keys'], permission: 'people.read' },
      { title: 'Groups', path: '/admin/groups', icon: UsersRound, keywords: ['teams', 'access', 'directory groups', 'permissions'], permission: 'people.read' },
      { title: 'Access', path: '/admin/access', icon: ShieldCheck, keywords: ['roles', 'permissions', 'rules', 'who may', 'deny', 'allow', 'check'], permission: 'access.read' },
      { title: 'Sign-in', path: '/admin/sign-in', icon: KeyRound, keywords: ['ldap', 'directory', 'active directory', '2fa'], permission: 'settings.read' },
    ],
  },
  {
    title: 'AI',
    space: 'admin',
    adminOnly: true,
    items: [
      { title: 'Models', path: '/admin/models', icon: Boxes, keywords: ['switch', 'load', 'llama', 'library', 'gguf', 'permissions'], permission: 'models.read' },
      { title: 'Tools', path: '/admin/tools', icon: Wrench, keywords: ['mcp', 'code-intel', 'image generation', 'calculator', 'permissions', 'openapi', 'api'], permission: 'tools.read' },
      { title: 'Plugins', path: '/admin/plugins', icon: Blocks, keywords: ['catalog', 'install', 'jira', 'gitlab issues', 'oauth', 'integrations'], permission: 'plugins.read' },
      { title: 'Safeguards', path: '/admin/safeguards', icon: ShieldAlert, keywords: ['guardrails', 'dlp', 'secrets', 'personal data', 'pii', 'blocked words', 'moderation', 'jailbreak', 'prompt injection', 'review', 'refused'], permission: 'safeguards.read' },
      { title: 'Knowledge', path: '/admin/knowledge', icon: Library, keywords: ['documents', 'wiki', 'gitlab', 'folder', 'website', 'confluence', 'sharepoint', 'search', 'embeddings', 'rag'], permission: 'knowledge.read' },
      { title: 'Data sources', path: '/admin/data-sources', icon: Database, keywords: ['ask your data', 'postgres', 'postgresql', 'sql', 'database', 'connection', 'read only', 'tables'], permission: 'data.manage', feature: 'data' },
    ],
  },
  {
    title: `${codeIntelName}`,
    space: 'admin',
    adminOnly: true,
    items: [
      { title: 'Indexing', path: '/admin/indexing', icon: Database, keywords: ['gitlab', 'repositories', 'index'], permission: 'code-intel.manage', feature: 'code-intel' },
      { title: 'Packs', path: '/admin/packs', icon: Package, keywords: ['knowledge packs'], permission: 'code-intel.manage', feature: 'code-intel' },
      { title: 'Explore', path: '/admin/explore', icon: Telescope, keywords: ['symbols', 'search code'], permission: 'code-intel.explore', feature: 'code-intel' },
    ],
  },
  {
    title: 'Governance',
    space: 'admin',
    adminOnly: true,
    items: [
      { title: 'Security', path: '/admin/security', icon: ScanSearch, keywords: ['vulnerabilities', 'cve', 'findings', 'exploited', 'sbom', 'risks accepted', 'secrets', 'advisories'], permission: 'reviews.admin', feature: 'code-studio' },
      { title: 'Compliance', path: '/admin/compliance', icon: BadgeCheck, keywords: ['iso 27001', 'soc 2', 'hipaa', 'gdpr', 'audit', 'controls', 'evidence', 'attest', 'policy', 'auditor'], permission: 'compliance.read' },
      { title: 'Audit log', path: '/admin/audit', icon: ScrollText, keywords: ['events', 'history', 'security'], permission: 'audit.read' },
      { title: 'Prompt log', path: '/admin/prompts', icon: ScrollText, keywords: ['prompts', 'questions', 'answers', 'review', 'what people asked', 'log'], permission: 'prompts.review' },
      { title: 'Feedback', path: '/admin/feedback', icon: MessageSquareHeart, keywords: ['ideas', 'problems', 'praise', 'send feedback', 'suggestions', 'bugs'], permission: 'feedback.manage' },
      { title: 'Broadcasts', path: '/admin/broadcasts', icon: Megaphone, keywords: ['announce', 'announcement', 'message everyone', 'banner', 'maintenance', 'notice'], permission: 'broadcasts.send' },
    ],
  },
  {
    title: 'System',
    space: 'admin',
    adminOnly: true,
    items: [
      { title: 'Settings', path: '/admin/settings', icon: Settings, keywords: ['configuration', 'config', 'env'], permission: 'settings.read' },
      { title: 'Storage', path: '/admin/storage', icon: HardDrive, keywords: ['disk', 'space', 'files', 'uploads', 'database', 'backups', 'quota', 'clean up', 'models', 'logs'], permission: 'storage.read' },
    ],
  },
]

/** What a person sees: an admin section's pages by the permissions they hold (all of them for an Owner), without the features that are off or that they may not use. */
export function visibleNavigation(me: { isAdmin: boolean; permissions?: string[]; featuresOff?: string[]; denied?: string[] }): NavSection[] {
  const held = new Set(me.permissions ?? [])
  const off = new Set(me.featuresOff ?? [])
  return navigation
    .map((s) => (!s.adminOnly || me.isAdmin ? s : { ...s, items: s.items.filter((i) => (i.permission && held.has(i.permission)) || i.anyOf?.some((k) => held.has(k))) }))
    .map((s) => ({ ...s, items: s.items.filter((i) => (!i.feature || !off.has(i.feature)) && mayOpen(me, i)) }))
    .filter((s) => s.items.length > 0)
}

/** Whether the person holds a feature permission the page needs (or it needs none). */
export const mayOpen = (me: { denied?: string[] }, item: NavItem) => !item.may || item.may.some((k) => !me.denied?.includes(k))

/** Pages inside a menu entry that need a permission of their own: opened by their link, never listed. */
export const innerPages: NavItem[] = [
  { title: 'Fix it', path: '/code/fixes', icon: Wrench, feature: 'code-studio', may: ['feature.fixit'] },
]

/** The nav item a path belongs to: the longest matching prefix. */
/** The tab a path's page is under; the workspace for one not in the sidebar. */
export function spaceOf(pathname: string): Space {
  const item = findNavItem(pathname)
  return (item && navigation.find((s) => s.items.includes(item))?.space) ?? 'work'
}

export function findNavItem(pathname: string): NavItem | undefined {
  const all = [...navigation.flatMap((s) => s.items), ...innerPages]
  return all
    .filter((i) => (i.path === '/' ? pathname === '/' : pathname === i.path || pathname.startsWith(i.path + '/')))
    .sort((a, b) => b.path.length - a.path.length)[0]
}

/** https://metrics.llm.example.com/ from llm.example.com: a service beside this one. */
export function serviceUrl(subdomain: string, location: Pick<Location, 'protocol' | 'host'> = window.location): string {
  return `${location.protocol}//${subdomain}.${location.host}/`
}
