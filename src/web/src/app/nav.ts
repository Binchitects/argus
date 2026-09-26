import {
  Activity,
  BarChart3,
  Boxes,
  Cpu,
  Database,
  Home,
  KeyRound,
  LayoutDashboard,
  LineChart,
  Logs,
  MessageSquare,
  Package,
  ScrollText,
  Siren,
  Settings,
  Telescope,
  Users,
  UsersRound,
  Wrench,
  type LucideIcon,
} from 'lucide-react'

export interface NavItem {
  title: string
  path: string
  icon: LucideIcon
  /** Words the command palette also matches. */
  keywords?: string[]
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
      { title: 'Usage & cost', path: '/usage', icon: BarChart3, keywords: ['tokens', 'spend', 'credit', 'budget'] },
    ],
  },
  {
    title: 'Administration',
    adminOnly: true,
    items: [
      { title: 'Overview', path: '/admin', icon: LayoutDashboard, keywords: ['status', 'health', 'services'] },
      { title: 'People', path: '/admin/people', icon: Users, keywords: ['users', 'accounts', 'credit', 'keys'] },
      { title: 'Groups', path: '/admin/groups', icon: UsersRound, keywords: ['teams', 'access', 'directory groups', 'permissions'] },
      { title: 'Sign-in', path: '/admin/sign-in', icon: KeyRound, keywords: ['ldap', 'directory', 'active directory', '2fa'] },
      { title: 'Models', path: '/admin/models', icon: Boxes, keywords: ['switch', 'load', 'llama', 'library', 'gguf', 'permissions'] },
      { title: 'Deployment', path: '/admin/model', icon: Cpu, keywords: ['model', 'llama', 'engine', 'gpu', 'prices', 'env'] },
      { title: 'Tools', path: '/admin/tools', icon: Wrench, keywords: ['mcp', 'argus', 'image generation', 'calculator', 'permissions'] },
      { title: 'Settings', path: '/admin/settings', icon: Settings, keywords: ['configuration', 'config', 'env'] },
      { title: 'Audit log', path: '/admin/audit', icon: ScrollText, keywords: ['events', 'history', 'security'] },
    ],
  },
  {
    title: 'Argus',
    adminOnly: true,
    items: [
      { title: 'Indexing', path: '/admin/indexing', icon: Database, keywords: ['gitlab', 'repositories', 'index'] },
      { title: 'Packs', path: '/admin/packs', icon: Package, keywords: ['knowledge packs'] },
      { title: 'Explore', path: '/admin/explore', icon: Telescope, keywords: ['symbols', 'search code'] },
    ],
  },
  {
    title: 'Observe',
    adminOnly: true,
    items: [
      { title: 'Monitoring', path: '/admin/monitoring', icon: Activity, keywords: ['prometheus', 'probes', 'health', 'services'] },
      { title: 'Dashboards', path: '/admin/dashboards', icon: LineChart, keywords: ['grafana', 'gpu', 'performance', 'metrics', 'charts'] },
      { title: 'Logs', path: '/admin/logs', icon: Logs, keywords: ['loki', 'errors', 'containers', 'tail'] },
      { title: 'Alerts', path: '/admin/alerts', icon: Siren, keywords: ['alertmanager', 'firing', 'rules', 'incidents'] },
    ],
  },
]

export function visibleNavigation(isAdmin: boolean): NavSection[] {
  return navigation.filter((s) => !s.adminOnly || isAdmin)
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
