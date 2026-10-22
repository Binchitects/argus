import { Activity, AppWindow, GitPullRequest, ListChecks, BarChart, BarChart3, Blocks, Bot, Boxes, CalendarClock, Database, HardDrive, Home, KeyRound, LayoutDashboard, Library, LineChart, Logs, MessageSquare, Package, Plug, Presentation, ScrollText, Settings, ShieldAlert, ShieldCheck, Siren, SquareSlash, Telescope, ThumbsUp, Timer, Trophy, Users, UsersRound, Workflow, Wrench, type LucideIcon } from 'lucide-react'

export interface NavItem {
  title: string
  path: string
  icon: LucideIcon
  /** Words the command palette also matches. */
  keywords?: string[]
  /** In an admin section: the permission that shows it (an Owner holds every one). */
  permission?: string
}

export interface NavSection {
  title: string
  adminOnly?: boolean
  items: NavItem[]
}

export const navigation: NavSection[] = [
  {
    title: 'Workspace',
    items: [
      { title: 'Home', path: '/', icon: Home },
      { title: 'Chat', path: '/chat', icon: MessageSquare, keywords: ['conversation', 'ask', 'model', 'argus'] },
      { title: 'Assistants', path: '/assistants', icon: Bot, keywords: ['gpts', 'gems', 'projects', 'instructions', 'knowledge', 'shared', 'team'] },
      { title: 'Prompts', path: '/prompts', icon: SquareSlash, keywords: ['prompt library', 'slash commands', 'templates', 'snippets', 'saved prompts'] },
      { title: 'Plans', path: '/plans', icon: ListChecks, keywords: ['plan', 'board', 'timeline', 'gantt', 'steps', 'project', 'kanban'] },
      { title: 'Apps', path: '/apps', icon: AppWindow, keywords: ['app', 'apps', 'tool', 'publish', 'page', 'component', 'gallery', 'remix'] },
      { title: 'Data', path: '/data', icon: BarChart, keywords: ['ask your data', 'sql', 'database', 'query', 'chart', 'dashboard', 'board', 'report', 'analytics', 'bi'] },
      { title: 'Decks', path: '/decks', icon: Presentation, keywords: ['slides', 'presentation', 'powerpoint', 'pptx', 'pdf', 'deck', 'keynote', 'template'] },
      { title: 'Agent Studio', path: '/studio', icon: Workflow, keywords: ['workflow', 'automation', 'agent', 'zapier', 'n8n', 'flow', 'trigger', 'approval', 'webhook', 'form'] },
      { title: 'Code', path: '/code', icon: GitPullRequest, keywords: ['review', 'merge request', 'mr', 'gitlab', 'diff', 'code assistant', 'branch'] },
      { title: 'Scheduled tasks', path: '/tasks', icon: CalendarClock, keywords: ['schedule', 'cron', 'daily', 'weekly', 'report', 'digest', 'webhook', 'email', 'notification'] },
      { title: 'Usage & cost', path: '/usage', icon: BarChart3, keywords: ['tokens', 'spend', 'credit', 'budget'] },
      { title: 'Leaderboard', path: '/leaderboard', icon: Trophy, keywords: ['arena', 'compare', 'models', 'elo', 'vote', 'ranking'] },
      { title: 'Connect your tools', path: '/setup', icon: Plug, keywords: ['api key', 'token', 'claude code', 'qwen code', 'opencode', 'continue', 'mcp', 'harness', 'sdk', 'certificate'] },
    ],
  },
  {
    title: 'Administration',
    adminOnly: true,
    items: [
      { title: 'Overview', path: '/admin', icon: LayoutDashboard, keywords: ['status', 'health', 'services'], permission: 'monitoring.read' },
      { title: 'People', path: '/admin/people', icon: Users, keywords: ['users', 'accounts', 'credit', 'keys'], permission: 'people.read' },
      { title: 'Safeguards', path: '/admin/safeguards', icon: ShieldAlert, keywords: ['guardrails', 'dlp', 'secrets', 'personal data', 'pii', 'blocked words', 'moderation', 'jailbreak', 'prompt injection', 'review', 'refused'], permission: 'safeguards.read' },
      { title: 'Access', path: '/admin/access', icon: ShieldCheck, keywords: ['roles', 'permissions', 'rules', 'who may', 'deny', 'allow', 'check'], permission: 'access.read' },
      { title: 'Groups', path: '/admin/groups', icon: UsersRound, keywords: ['teams', 'access', 'directory groups', 'permissions'], permission: 'people.read' },
      { title: 'Sign-in', path: '/admin/sign-in', icon: KeyRound, keywords: ['ldap', 'directory', 'active directory', '2fa'], permission: 'settings.read' },
      { title: 'Models', path: '/admin/models', icon: Boxes, keywords: ['switch', 'load', 'llama', 'library', 'gguf', 'permissions'], permission: 'models.read' },
      { title: 'Tools', path: '/admin/tools', icon: Wrench, keywords: ['mcp', 'argus', 'image generation', 'calculator', 'permissions', 'openapi', 'api'], permission: 'tools.read' },
      { title: 'Plugins', path: '/admin/plugins', icon: Blocks, keywords: ['catalog', 'install', 'jira', 'gitlab issues', 'oauth', 'integrations'], permission: 'plugins.read' },
      { title: 'Knowledge', path: '/admin/knowledge', icon: Library, keywords: ['documents', 'wiki', 'gitlab', 'folder', 'website', 'confluence', 'sharepoint', 'search', 'embeddings', 'rag'], permission: 'knowledge.read' },
      { title: 'Data sources', path: '/admin/data-sources', icon: Database, keywords: ['ask your data', 'postgres', 'postgresql', 'sql', 'database', 'connection', 'read only', 'tables'], permission: 'data.manage' },
      { title: 'Storage', path: '/admin/storage', icon: HardDrive, keywords: ['disk', 'space', 'files', 'uploads', 'database', 'backups', 'quota', 'clean up', 'models', 'logs'], permission: 'storage.read' },
      { title: 'Settings', path: '/admin/settings', icon: Settings, keywords: ['configuration', 'config', 'env'], permission: 'settings.read' },
      { title: 'Audit log', path: '/admin/audit', icon: ScrollText, keywords: ['events', 'history', 'security'], permission: 'audit.read' },
      { title: 'Quality', path: '/admin/quality', icon: ThumbsUp, keywords: ['feedback', 'thumbs', 'ratings', 'arena', 'leaderboard', 'answers'], permission: 'monitoring.read' },
    ],
  },
  {
    title: 'Argus',
    adminOnly: true,
    items: [
      { title: 'Indexing', path: '/admin/indexing', icon: Database, keywords: ['gitlab', 'repositories', 'index'], permission: 'argus.manage' },
      { title: 'Packs', path: '/admin/packs', icon: Package, keywords: ['knowledge packs'], permission: 'argus.manage' },
      { title: 'Explore', path: '/admin/explore', icon: Telescope, keywords: ['symbols', 'search code'], permission: 'argus.explore' },
    ],
  },
  {
    title: 'Observe',
    adminOnly: true,
    items: [
      { title: 'Monitoring', path: '/admin/monitoring', icon: Activity, keywords: ['prometheus', 'probes', 'health', 'services'], permission: 'monitoring.read' },
      { title: 'Dashboards', path: '/admin/dashboards', icon: LineChart, keywords: ['gpu', 'performance', 'metrics', 'charts'], permission: 'monitoring.read' },
      { title: 'Logs', path: '/admin/logs', icon: Logs, keywords: ['loki', 'errors', 'containers', 'tail'], permission: 'logs.read' },
      { title: 'Alerts', path: '/admin/alerts', icon: Siren, keywords: ['alertmanager', 'firing', 'rules', 'incidents'], permission: 'monitoring.read' },
      { title: 'Traces', path: '/admin/traces', icon: Timer, keywords: ['slow', 'answers', 'latency', 'timeline', 'sub-agents', 'speed', 'tokens'], permission: 'traces.read' },
    ],
  },
]

/** What a person sees: an admin section's pages by the permissions they hold (all of them for an Owner). */
export function visibleNavigation(me: { isAdmin: boolean; permissions?: string[] }): NavSection[] {
  const held = new Set(me.permissions ?? [])
  return navigation
    .map((s) => (!s.adminOnly || me.isAdmin ? s : { ...s, items: s.items.filter((i) => i.permission && held.has(i.permission)) }))
    .filter((s) => s.items.length > 0)
}

/** The nav item a path belongs to: the longest matching prefix. */
export function findNavItem(pathname: string): NavItem | undefined {
  const all = navigation.flatMap((s) => s.items)
  return all
    .filter((i) => (i.path === '/' ? pathname === '/' : pathname === i.path || pathname.startsWith(i.path + '/')))
    .sort((a, b) => b.path.length - a.path.length)[0]
}

/** https://metrics.llm.example.com/ from llm.example.com: a service beside this one. */
export function serviceUrl(subdomain: string, location: Pick<Location, 'protocol' | 'host'> = window.location): string {
  return `${location.protocol}//${subdomain}.${location.host}/`
}
