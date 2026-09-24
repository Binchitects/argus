import {
  Activity,
  BarChart3,
  Cpu,
  Database,
  Home,
  KeyRound,
  LayoutDashboard,
  LineChart,
  MessageSquare,
  Package,
  ScrollText,
  Settings,
  Telescope,
  Users,
  type LucideIcon,
} from 'lucide-react'

export interface NavItem {
  title: string
  path: string
  icon: LucideIcon
  /** Words the command palette also matches. */
  keywords?: string[]
  /** Built in this web already; otherwise the page points to the current app. */
  ready?: boolean
  /** The plan phase that builds it here. */
  phase?: string
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
      { title: 'Home', path: '/', icon: Home, ready: true },
      { title: 'Chat', path: '/chat', icon: MessageSquare, keywords: ['conversation', 'ask', 'model', 'argus'], phase: '3C' },
      { title: 'Usage & cost', path: '/usage', icon: BarChart3, keywords: ['tokens', 'spend', 'credit', 'budget'], ready: true },
    ],
  },
  {
    title: 'Administration',
    adminOnly: true,
    items: [
      { title: 'Overview', path: '/admin', icon: LayoutDashboard, keywords: ['status', 'health', 'services'], ready: true },
      { title: 'People', path: '/admin/people', icon: Users, keywords: ['users', 'accounts', 'credit', 'keys'], ready: true },
      { title: 'Sign-in', path: '/admin/sign-in', icon: KeyRound, keywords: ['ldap', 'directory', 'active directory', '2fa'], ready: true },
      { title: 'Model', path: '/admin/model', icon: Cpu, keywords: ['llama', 'engine', 'gpu', 'prices'], ready: true },
      { title: 'Settings', path: '/admin/settings', icon: Settings, keywords: ['configuration', 'config', 'env'], ready: true },
      { title: 'Audit log', path: '/admin/audit', icon: ScrollText, keywords: ['events', 'history', 'security'], ready: true },
    ],
  },
  {
    title: 'Argus',
    adminOnly: true,
    items: [
      { title: 'Indexing', path: '/admin/indexing', icon: Database, keywords: ['gitlab', 'repositories', 'index'], ready: true },
      { title: 'Packs', path: '/admin/packs', icon: Package, keywords: ['knowledge packs'], ready: true },
      { title: 'Explore', path: '/admin/explore', icon: Telescope, keywords: ['symbols', 'search code'], ready: true },
    ],
  },
  {
    title: 'Observe',
    adminOnly: true,
    items: [
      { title: 'Monitoring', path: '/admin/monitoring', icon: Activity, keywords: ['prometheus', 'alerts', 'probes'], ready: true },
      { title: 'Dashboards', path: '/dashboards', icon: LineChart, keywords: ['grafana', 'gpu', 'performance'], phase: '5' },
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

/**
 * Where the current app serves a page while this web is being built at next.<domain>.
 * On the real domain (after the switch) it is this origin.
 */
export function currentAppUrl(path: string, location: Pick<Location, 'protocol' | 'host'> = window.location): string {
  const host = location.host.startsWith('next.') ? location.host.slice(5) : location.host
  return `${location.protocol}//${host}${path}`
}
