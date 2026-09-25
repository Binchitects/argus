import { useQuery } from '@tanstack/react-query'
import { ChevronsLeft, ChevronsRight, Menu, Search } from 'lucide-react'
import { useEffect, useState } from 'react'
import { Link, NavLink, Outlet, useLocation, useMatches, useNavigate } from 'react-router'
import { Button } from '@/components/ui/button'
import { Kbd } from '@/components/ui/kbd'
import { Sheet, SheetContent, SheetDescription, SheetTitle } from '@/components/ui/sheet'
import { Skeleton } from '@/components/ui/skeleton'
import { Tooltip } from '@/components/ui/tooltip'
import { infoQuery, meQuery, type Me } from '@/lib/api'
import { cn } from '@/lib/utils'
import { CommandMenu } from './command-menu'
import { findNavItem, visibleNavigation, type NavSection } from './nav'
import { UserMenu } from './user-menu'

function readCollapsed(): boolean {
  try {
    return localStorage.getItem('sidebar') === 'collapsed'
  } catch {
    return false
  }
}

/** Signed-in pages: sidebar, top bar, command palette. Sends anyone signed out to the sign-in page. */
export function Shell() {
  const me = useQuery(meQuery)
  const location = useLocation()
  const navigate = useNavigate()

  useEffect(() => {
    if (me.data === null) navigate(`/login?rd=${encodeURIComponent(location.pathname + location.search)}`, { replace: true })
  }, [me.data, location.pathname, location.search, navigate])

  if (me.isPending || !me.data) return <ShellSkeleton />
  return <SignedIn me={me.data} />
}

function SignedIn({ me }: { me: Me }) {
  const info = useQuery(infoQuery)
  const [collapsed, setCollapsed] = useState(readCollapsed)
  const [mobileOpen, setMobileOpen] = useState(false)
  const [paletteOpen, setPaletteOpen] = useState(false)
  const sections = visibleNavigation(me.isAdmin)
  const name = info.data?.name ?? 'LLM Service'

  const location = useLocation()
  const fullBleed = useMatches().some((m) => (m.handle as { fullBleed?: boolean } | undefined)?.fullBleed)
  const pageTitle = findNavItem(location.pathname)?.title
  useEffect(() => {
    // The chat names its tab after the conversation itself.
    if (fullBleed) return
    document.title = pageTitle && location.pathname !== '/' ? `${pageTitle} · ${name}` : name
  }, [pageTitle, name, location.pathname, fullBleed])

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key.toLowerCase() === 'k' && (e.metaKey || e.ctrlKey)) {
        e.preventDefault()
        setPaletteOpen((o) => !o)
      }
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [])

  const toggle = () => {
    setCollapsed((c) => {
      try {
        localStorage.setItem('sidebar', c ? 'expanded' : 'collapsed')
      } catch {
        // not remembered in a private window
      }
      return !c
    })
  }

  return (
    <div className="flex min-h-dvh">
      <a href="#main" className="sr-only z-50 rounded-md bg-primary px-3 py-2 text-primary-foreground focus:not-sr-only focus:fixed focus:top-2 focus:left-2">
        Skip to content
      </a>
      {/* The column stretches with the page (its background never ends); the panel inside stays in view. */}
      <aside
        className={cn('hidden shrink-0 border-r border-sidebar-border bg-sidebar text-sidebar-foreground transition-[width] duration-200 md:block', collapsed ? 'w-14' : 'w-60')}
        aria-label="Sidebar"
      >
        <div className="sticky top-0 flex h-dvh flex-col">
          <Brand name={name} collapsed={collapsed} />
          <SidebarNav sections={sections} collapsed={collapsed} />
          <div className="border-t border-sidebar-border p-2">
            <Button variant="ghost" size={collapsed ? 'icon-sm' : 'sm'} className={cn('text-muted-foreground', !collapsed && 'w-full justify-start')} onClick={toggle} aria-label={collapsed ? 'Expand sidebar' : 'Collapse sidebar'}>
              {collapsed ? <ChevronsRight /> : <><ChevronsLeft /> Collapse</>}
            </Button>
            {!collapsed && info.data && <p className="px-2 pt-1 text-[0.6875rem] text-muted-foreground" aria-label="Version">v{info.data.version}</p>}
          </div>
        </div>
      </aside>

      <Sheet open={mobileOpen} onOpenChange={setMobileOpen}>
        <SheetContent side="left" className="w-72 gap-0 bg-sidebar p-0">
          <SheetTitle className="sr-only">Navigation</SheetTitle>
          <SheetDescription className="sr-only">Pages of {name}</SheetDescription>
          <Brand name={name} collapsed={false} />
          <SidebarNav sections={sections} collapsed={false} onNavigate={() => setMobileOpen(false)} />
        </SheetContent>
      </Sheet>

      <div className="flex min-w-0 flex-1 flex-col">
        <header className="sticky top-0 z-30 flex h-14 items-center gap-2 border-b bg-background/85 px-3 backdrop-blur supports-[backdrop-filter]:bg-background/70 sm:px-5">
          <Button variant="ghost" size="icon-sm" className="md:hidden" onClick={() => setMobileOpen(true)} aria-label="Open navigation">
            <Menu />
          </Button>
          <Breadcrumbs />
          <div className="ml-auto flex items-center gap-2">
            <Button variant="outline" size="sm" className="h-8 gap-2 text-muted-foreground sm:w-56 sm:justify-start" onClick={() => setPaletteOpen(true)} aria-label="Search and commands">
              <Search />
              <span className="hidden sm:inline">Search…</span>
              <Kbd className="ml-auto hidden sm:inline-flex">Ctrl K</Kbd>
            </Button>
            <UserMenu me={me} />
          </div>
        </header>
        <main id="main" tabIndex={-1} className={cn('w-full flex-1 outline-none', fullBleed ? 'min-h-0' : 'mx-auto max-w-(--page-max) px-4 py-6 sm:px-6 lg:px-8')}>
          <Outlet context={me} />
        </main>
      </div>
      <CommandMenu open={paletteOpen} onOpenChange={setPaletteOpen} me={me} />
    </div>
  )
}

function Brand({ name, collapsed }: { name: string; collapsed: boolean }) {
  return (
    <Link to="/" className={cn('flex h-14 shrink-0 items-center gap-2.5 border-b border-sidebar-border px-4 font-semibold', collapsed && 'justify-center px-0')}>
      <img src="/favicon.svg" alt="" className="size-7 rounded-md" />
      {!collapsed && <span className="truncate">{name}</span>}
    </Link>
  )
}

function SidebarNav({ sections, collapsed, onNavigate }: { sections: NavSection[]; collapsed: boolean; onNavigate?: () => void }) {
  return (
    <nav aria-label="Main" className="flex-1 overflow-y-auto px-2 py-3">
      {sections.map((s) => (
        <div key={s.title} className="mb-4">
          {collapsed ? <div className="mx-2 mb-2 h-px bg-sidebar-border first:hidden" /> : <p className="px-2 pb-1.5 text-[0.6875rem] font-semibold tracking-wider text-muted-foreground uppercase">{s.title}</p>}
          <ul className="grid gap-0.5">
            {s.items.map((item) => {
              const link = (
                <NavLink
                  to={item.path}
                  end={item.path === '/' || item.path === '/admin'}
                  className={({ isActive }) =>
                    cn(
                      'flex h-8 items-center gap-2.5 rounded-md px-2 text-sm font-medium transition-colors hover:bg-sidebar-accent',
                      isActive ? 'bg-sidebar-accent text-foreground' : 'text-sidebar-foreground/85',
                      collapsed && 'justify-center px-0',
                    )
                  }
                  aria-label={collapsed ? item.title : undefined}
                  onClick={onNavigate}
                >
                  <item.icon className="size-4 shrink-0" aria-hidden="true" />
                  {!collapsed && <span className="truncate">{item.title}</span>}
                </NavLink>
              )
              return <li key={item.path}>{collapsed ? <Tooltip content={item.title} side="right">{link}</Tooltip> : link}</li>
            })}
          </ul>
        </div>
      ))}
    </nav>
  )
}

function Breadcrumbs() {
  const { pathname } = useLocation()
  const item = findNavItem(pathname)
  const section = item && visibleNavigation(true).find((s) => s.items.includes(item))
  const title = pathname === '/account' ? 'Your account' : pathname === '/design' ? 'Design system' : item?.title
  return (
    <nav aria-label="Breadcrumb" className="min-w-0">
      <ol className="flex items-center gap-1.5 truncate text-sm text-muted-foreground">
        {section && section.title !== 'Workspace' && (
          <>
            <li className="hidden sm:block">{section.title}</li>
            <li className="hidden sm:block" aria-hidden="true">/</li>
          </>
        )}
        {title && (
          <li className="truncate font-medium text-foreground" aria-current="page">
            {title}
          </li>
        )}
      </ol>
    </nav>
  )
}

function ShellSkeleton() {
  return (
    <div className="flex min-h-dvh" aria-busy="true" aria-label="Loading">
      <div className="hidden w-60 border-r bg-sidebar p-4 md:block">
        <Skeleton className="mb-8 h-7 w-32" />
        {Array.from({ length: 6 }, (_, i) => (
          <Skeleton key={i} className="mb-3 h-5 w-40" />
        ))}
      </div>
      <div className="flex-1 p-8">
        <Skeleton className="mb-4 h-8 w-64" />
        <Skeleton className="h-40 w-full" />
      </div>
    </div>
  )
}
