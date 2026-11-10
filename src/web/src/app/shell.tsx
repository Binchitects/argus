import { useQuery } from '@tanstack/react-query'
import { BookOpen, ChevronsLeft, ChevronsRight, CodeXml, Menu, Search } from 'lucide-react'
import { useCallback, useEffect, useRef, useState } from 'react'
import { Link, Outlet, useLocation, useMatch, useMatches, useNavigate, useResolvedPath } from 'react-router'
import { BroadcastBanner, type ActiveBroadcast } from '@/components/app/broadcasts'
import { confetti, useKonami, useQuickClicks } from '@/components/app/games-kit'
import { NotificationBell } from '@/components/app/notifications'
import { Button } from '@/components/ui/button'
import { Kbd } from '@/components/ui/kbd'
import { Sheet, SheetContent, SheetDescription, SheetTitle } from '@/components/ui/sheet'
import { Skeleton } from '@/components/ui/skeleton'
import { toast } from '@/components/ui/toaster'
import { Tooltip } from '@/components/ui/tooltip'
import { HelpButton, HelpPanel } from '@/help/help-button'
import { EmptyState } from '@/components/ui/empty-state'
import { api, infoQuery, meQuery, type AppInfo, type Me } from '@/lib/api'
import { AcceptNotice } from './accept-notice'
import { useFollowColorTheme } from './color-theme'
import { continueTo } from './continue'
import { SetUpTwoFactor } from './set-up-two-factor'
import { cn } from '@/lib/utils'
import { CommandMenu } from './command-menu'
import { useAppIcon } from '@/lib/app-icon'
import { findNavItem, mayOpen, spaceOf, spaces, spaceTitle, visibleNavigation, type NavItem, type NavSection, type Space } from './nav'
import { UserMenu } from './user-menu'
import { WhatsNew } from './whats-new'
import { useChatWatch } from '@/pages/chat/watch'
import { productName } from '@/lib/brand'

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
  useFollowColorTheme(me.data?.colorTheme)
  // The person's chats change elsewhere (another device, Code Arena): read again as it happens.
  useChatWatch(!!me.data)

  useEffect(() => {
    if (me.data === null) navigate(`/login?rd=${encodeURIComponent(location.pathname + location.search)}`, { replace: true })
  }, [me.data, location.pathname, location.search, navigate])

  // Sent here by the app to take a step of signing in first: once nothing holds it back, on to where it was going.
  const next = continueTo(location)
  const ready = me.data && !me.data.mustAcceptNotice && !me.data.mustSetUpTwoFactor
  useEffect(() => {
    if (ready && next) window.location.replace(next)
  }, [ready, next])

  if (me.isPending || !me.data) return <ShellSkeleton />
  if (me.data.mustAcceptNotice) return <AcceptNotice me={me.data} />
  if (me.data.mustSetUpTwoFactor) return <SetUpTwoFactor me={me.data} />
  if (next) return <ShellSkeleton />
  return <SignedIn me={me.data} />
}

function SignedIn({ me }: { me: Me }) {
  const info = useQuery({ ...infoQuery, refetchInterval: 5 * 60_000, refetchOnWindowFocus: true })
  useNewVersion(info.data?.version)
  const [collapsed, setCollapsed] = useState(readCollapsed)
  const [mobileOpen, setMobileOpen] = useState(false)
  const [paletteOpen, setPaletteOpen] = useState(false)
  const [helpOpen, setHelpOpen] = useState(false)
  // The newest release's tour: once by itself after a deploy, again from the account menu or the palette.
  const [whatsNew, setWhatsNew] = useState(false)
  const sections = visibleNavigation(me)
  const name = info.data?.name ?? `${productName}`
  const icon = useAppIcon(info.data?.icon)
  // The old cheat code, anywhere but a text box.
  useKonami(useCallback(() => {
    confetti()
    toast('Cheat code accepted', { description: 'Thirty lives, no tokens charged. (Only the first part is true.)' })
  }, []))

  const location = useLocation()
  const fullBleed = useMatches().some((m) => (m.handle as { fullBleed?: boolean } | undefined)?.fullBleed)
  const page = findNavItem(location.pathname)
  const pageTitle = page?.title ?? (isManual(location.pathname) ? 'Manual' : undefined)
  // A page of a feature that is off here: what it is and how it is turned on, not a page that cannot work.
  const notInstalled = page?.feature && me.featuresOff?.includes(page.feature) ? page : undefined
  // A page of a feature the person may not use (Admin → Access): said so, not a page whose every request is refused.
  const notAllowed = page && !mayOpen(me, page) ? page : undefined
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
          <Brand name={name} icon={icon} collapsed={collapsed} />
          <SidebarNav sections={sections} collapsed={collapsed} />
          <div className="border-t border-sidebar-border p-2">
            <ManualLink collapsed={collapsed} />
            <Button variant="ghost" size={collapsed ? 'icon-sm' : 'sm'} className={cn('text-muted-foreground', !collapsed && 'w-full justify-start')} onClick={toggle} aria-label={collapsed ? 'Expand sidebar' : 'Collapse sidebar'}>
              {collapsed ? <ChevronsRight /> : <><ChevronsLeft /> Collapse</>}
            </Button>
            {info.data && <VersionSource info={info.data} collapsed={collapsed} />}
          </div>
        </div>
      </aside>

      <Sheet open={mobileOpen} onOpenChange={setMobileOpen}>
        <SheetContent side="left" className="w-72 gap-0 bg-sidebar p-0">
          <SheetTitle className="sr-only">Navigation</SheetTitle>
          <SheetDescription className="sr-only">Pages of {name}</SheetDescription>
          <Brand name={name} icon={icon} collapsed={false} />
          <SidebarNav sections={sections} collapsed={false} onNavigate={() => setMobileOpen(false)} />
          <div className="border-t border-sidebar-border p-2">
            <ManualLink collapsed={false} onNavigate={() => setMobileOpen(false)} />
            {info.data && <VersionSource info={info.data} collapsed={false} />}
          </div>
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
            <HelpButton open={helpOpen} onOpenChange={setHelpOpen} />
            <NotificationBell />
            <UserMenu me={me} onWhatsNew={() => setWhatsNew(true)} />
          </div>
        </header>
        <div className="mx-auto w-full max-w-(--page-max) px-4 pt-3 empty:hidden sm:px-6 lg:px-8">
          <BroadcastBanner get={(signal) => api<ActiveBroadcast[]>('/api/broadcasts', { signal })} dismiss={(id) => api(`/api/broadcasts/${id}/dismiss`, { body: {} })} />
        </div>
        <main id="main" tabIndex={-1} className={cn('w-full flex-1 outline-none', fullBleed ? 'min-h-0' : 'mx-auto max-w-(--page-max) px-4 py-6 sm:px-6 lg:px-8')}>
          {notInstalled ? (
            <NotInstalled item={notInstalled} admin={me.isAdmin} />
          ) : notAllowed ? (
            <NotAllowed item={notAllowed} />
          ) : fullBleed ? (
            <Outlet context={me} />
          ) : (
            <div key={location.pathname} className="animate-page">
              <Outlet context={me} />
            </div>
          )}
        </main>
      </div>
      <HelpPanel open={helpOpen} onOpenChange={setHelpOpen} />
      <CommandMenu open={paletteOpen} onOpenChange={setPaletteOpen} me={me} onWhatsNew={() => setWhatsNew(true)} />
      <WhatsNew open={whatsNew} onOpenChange={setWhatsNew} />
    </div>
  )
}

/** A page whose feature is off here (deploy/features): the installer turns it on. */
function NotInstalled({ item, admin }: { item: NavItem; admin: boolean }) {
  return (
    <EmptyState icon={item.icon} title={`${item.title} is not installed here`} className="mx-auto mt-16 max-w-lg">
      {admin ? (
        <>
          Its feature, <code>{item.feature}</code>, is off. The installer turns it on: <code>./arenactl repair --with {item.feature}</code> from the checkout, or <code>sh arena-VERSION-offline.run repair --with {item.feature}</code> with the offline installer.
        </>
      ) : (
        'This feature is off on this server. Ask an admin if you need it.'
      )}
    </EmptyState>
  )
}

/** A page of a feature the person may not use: whom to ask. */
function NotAllowed({ item }: { item: NavItem }) {
  return (
    <EmptyState icon={item.icon} title={`${item.title} is not among what you may use`} className="mx-auto mt-16 max-w-lg">
      An admin gives it on the Access page. Ask for it under <Link to="/account" className="underline underline-offset-2">Your account</Link> → Ask for access.
    </EmptyState>
  )
}

/**
 * The version, and the source offered to everyone who uses the app (the AGPL's section 13,
 * LICENSING.md 7(b)): a line under the sidebar and the phone's navigation, an icon in the
 * collapsed sidebar.
 */
function VersionSource({ info, collapsed }: { info: AppInfo; collapsed: boolean }) {
  const licence = `Licensed under the ${info.license ?? 'AGPL-3.0'}: its source is offered to everyone who uses it`
  if (collapsed)
    return info.source ? (
      <Tooltip content={`v${info.version} · Source`} side="right">
        <a href={info.source} target="_blank" rel="noreferrer" aria-label="Source" className="mt-1 flex size-8 items-center justify-center rounded-md text-muted-foreground hover:bg-sidebar-accent hover:text-foreground">
          <CodeXml className="size-4" aria-hidden="true" />
        </a>
      </Tooltip>
    ) : null
  return (
    <p className="px-2 pt-1 text-[0.6875rem] text-muted-foreground">
      <span aria-label="Version">v{info.version}</span>
      {info.source && (
        <>
          {' · '}
          <a href={info.source} target="_blank" rel="noreferrer" className="underline-offset-2 hover:underline" title={licence}>
            Source
          </a>
        </>
      )}
    </p>
  )
}

const isManual = (pathname: string) => pathname === '/help' || pathname.startsWith('/help/')

/** The manual, at the foot of the sidebar: a line, or an icon in the collapsed sidebar. */
function ManualLink({ collapsed, onNavigate }: { collapsed: boolean; onNavigate?: () => void }) {
  const { pathname } = useLocation()
  const link = (
    <Link
      to="/help"
      onClick={onNavigate}
      aria-current={isManual(pathname) ? 'page' : undefined}
      aria-label={collapsed ? 'Manual' : undefined}
      className={cn(
        'mb-1 flex h-8 items-center gap-2 rounded-md px-2 text-sm text-muted-foreground hover:bg-sidebar-accent hover:text-foreground aria-[current=page]:bg-sidebar-accent aria-[current=page]:text-foreground',
        collapsed && 'mx-auto size-8 justify-center px-0',
      )}
    >
      <BookOpen className="size-4 shrink-0" aria-hidden="true" />
      {!collapsed && 'Manual'}
    </Link>
  )
  return collapsed ? (
    <Tooltip content="Manual" side="right">
      {link}
    </Tooltip>
  ) : (
    link
  )
}

/** What the eye says when its logo is clicked seven times quickly. */
const eyeSays = [
  'The owl sees in the dark. This one has also read your code.',
  'Wise, not sleepy. The GPU sometimes naps.',
  'Seven clicks. The owl blinks back.',
]

function Brand({ name, icon, collapsed }: { name: string; icon: string; collapsed: boolean }) {
  const said = useRef(0)
  const blink = useQuickClicks(7, useCallback(() => {
    confetti()
    toast(eyeSays[said.current++ % eyeSays.length])
  }, []))
  return (
    <Link to="/" onClick={blink} className={cn('flex h-14 shrink-0 items-center gap-2.5 border-b border-sidebar-border px-4 font-semibold', collapsed && 'justify-center px-0')}>
      <img src={icon} alt="" className="size-7 rounded-md object-contain" />
      {!collapsed && <span className="truncate">{name}</span>}
    </Link>
  )
}

/**
 * One entry of the sidebar. Its class is worked out here, a plain string: the
 * collapsed sidebar wraps it in a tooltip trigger, whose slot joins class names
 * as strings, so a NavLink's class function would be turned into its own source.
 */
function SidebarLink({ item, collapsed, onNavigate }: { item: NavItem; collapsed: boolean; onNavigate?: () => void }) {
  const resolved = useResolvedPath(item.path)
  const active = useMatch({ path: resolved.pathname, end: item.path === '/' || item.path === '/admin' }) !== null
  const link = (
    <Link
      to={item.path}
      aria-current={active ? 'page' : undefined}
      aria-label={collapsed ? item.title : undefined}
      onClick={onNavigate}
      className={cn(
        'flex h-8 items-center gap-2.5 rounded-md px-2 text-sm font-medium transition-[color,background-color,scale] duration-150 hover:bg-sidebar-accent active:scale-[0.98] [&>svg]:transition-transform hover:[&>svg]:scale-110',
        active ? 'bg-sidebar-accent text-foreground' : 'text-sidebar-foreground/85',
        collapsed && 'mx-auto size-9 justify-center px-0',
      )}
    >
      <item.icon className="size-4 shrink-0" aria-hidden="true" />
      {!collapsed && <span className="truncate">{item.title}</span>}
    </Link>
  )
  return collapsed ? (
    <Tooltip content={item.title} side="right">
      {link}
    </Tooltip>
  ) : (
    link
  )
}

/**
 * The sidebar's tabs, each a sidebar of its own (the tools and chats, the dashboards, the administration): only those the
 * person has pages in, none for one who has the workspace only. The tab follows the page opened; another is a click away.
 */
function useSpace(sections: NavSection[]) {
  const { pathname } = useLocation()
  const available = spaces.filter((sp) => sections.some((s) => s.space === sp.id))
  // A tab chosen by hand holds while its page stays open; opening a page shows that page's tab.
  const [picked, setPicked] = useState<{ path: string; space: Space } | null>(null)
  const chosen = picked?.path === pathname ? picked.space : spaceOf(pathname)
  const space = available.some((a) => a.id === chosen) ? chosen : 'work'
  return { space, setSpace: (s: Space) => setPicked({ path: pathname, space: s }), available }
}

function SpaceTabs({ available, space, onChoose, collapsed }: { available: typeof spaces; space: Space; onChoose: (s: Space) => void; collapsed: boolean }) {
  return (
    <div role="tablist" aria-label="Sidebar" aria-orientation={collapsed ? 'vertical' : 'horizontal'} className={cn('border-b border-sidebar-border p-2', collapsed ? 'grid gap-1' : 'grid grid-cols-3 gap-1')}>
      {available.map((sp) => {
        const selected = sp.id === space
        const tab = (
          <button
            key={sp.id}
            type="button"
            role="tab"
            aria-selected={selected}
            aria-label={collapsed ? sp.title : undefined}
            onClick={() => onChoose(sp.id)}
            className={cn(
              'flex items-center justify-center rounded-md text-xs font-medium transition-colors duration-150 hover:bg-sidebar-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none',
              collapsed ? 'mx-auto size-9' : 'h-12 flex-col gap-1 px-1',
              selected ? 'bg-sidebar-accent text-foreground' : 'text-muted-foreground',
            )}
          >
            <sp.icon className="size-4 shrink-0" aria-hidden="true" />
            {!collapsed && <span className="truncate">{sp.title}</span>}
          </button>
        )
        return collapsed ? (
          <Tooltip key={sp.id} content={sp.title} side="right">
            {tab}
          </Tooltip>
        ) : (
          tab
        )
      })}
    </div>
  )
}

function SidebarNav({ sections, collapsed, onNavigate }: { sections: NavSection[]; collapsed: boolean; onNavigate?: () => void }) {
  const { space, setSpace, available } = useSpace(sections)
  const shown = sections.filter((s) => s.space === space)
  return (
    <>
      {available.length > 1 && <SpaceTabs available={available} space={space} onChoose={setSpace} collapsed={collapsed} />}
      <nav aria-label="Main" className="flex-1 overflow-y-auto px-2 py-3">
        {shown.map((s, i) => (
          <div key={s.title} className={collapsed ? 'mb-2' : 'mb-4'}>
            {collapsed ? i > 0 && <div className="mx-2 mb-2 h-px bg-sidebar-border" aria-hidden="true" /> : <p className="px-2 pb-1.5 text-[0.6875rem] font-semibold tracking-wider text-muted-foreground uppercase">{s.title}</p>}
            <ul className="grid gap-0.5">
              {s.items.map((item) => (
                <li key={item.path}>
                  <SidebarLink item={item} collapsed={collapsed} onNavigate={onNavigate} />
                </li>
              ))}
            </ul>
          </div>
        ))}
      </nav>
    </>
  )
}

function Breadcrumbs() {
  const { pathname } = useLocation()
  const item = findNavItem(pathname)
  const section = item && visibleNavigation({ isAdmin: true }).find((s) => s.items.includes(item))
  const title = pathname === '/account' ? 'Your account' : pathname === '/design' ? 'Design system' : isManual(pathname) ? 'Manual' : item?.title
  return (
    <nav aria-label="Breadcrumb" className="min-w-0">
      <ol className="flex items-center gap-1.5 truncate text-sm text-muted-foreground">
        {section && section.space !== 'work' && (
          <>
            <li className="hidden sm:block">{spaceTitle(section.space)}</li>
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

/**
 * A new version deployed while the page is open: the page itself is still the old one,
 * so it says so, once, with a Reload.
 */
function useNewVersion(version: string | undefined) {
  const loaded = useRef<string | null>(null)
  useEffect(() => {
    if (!version) return
    if (loaded.current === null) loaded.current = version
    else if (version !== loaded.current)
      toast(`Version ${version} is deployed`, {
        id: 'new-version',
        description: 'This page is still the one before it: reload to use the new one.',
        duration: Infinity,
        action: { label: 'Reload', onClick: () => window.location.reload() },
      })
  }, [version])
}
