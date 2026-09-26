import { LogOut, Monitor, Moon, Sun, UserRound, type LucideIcon } from 'lucide-react'
import { useMemo, useState } from 'react'
import { useNavigate } from 'react-router'
import { CommandDialog, CommandEmpty, CommandGroup, CommandInput, CommandItem, CommandList } from '@/components/ui/command'
import type { Me } from '@/lib/api'
import { commandFilter } from '@/lib/command-filter'
import { useTheme } from '@/lib/theme'
import { visibleNavigation } from './nav'
import { useSignOut } from './use-sign-out'

interface Command {
  id: string
  title: string
  group: string
  icon: LucideIcon
  keywords?: string[]
  run: () => void
}

/**
 * Ctrl/⌘ K: go anywhere, change the theme, sign out. While searching, the
 * matches are one list, best first (ranked here: cmdk reorders items but not
 * the groups around them, so a keyword hit in an earlier group would win).
 */
export function CommandMenu({ open, onOpenChange, me }: { open: boolean; onOpenChange: (o: boolean) => void; me: Me }) {
  const navigate = useNavigate()
  const signOut = useSignOut()
  const { setPreference } = useTheme()
  const [search, setSearch] = useState('')

  const commands = useMemo<Command[]>(
    () => [
      ...visibleNavigation(me.isAdmin).flatMap((s) =>
        s.items.map((i) => ({ id: i.path, title: i.title, group: s.title, icon: i.icon, keywords: i.keywords, run: () => navigate(i.path) })),
      ),
      { id: 'account', title: 'Your account', group: 'Account', icon: UserRound, keywords: ['password', '2fa', 'api key', 'profile'], run: () => navigate('/account') },
      { id: 'light', title: 'Light theme', group: 'Account', icon: Sun, keywords: ['appearance'], run: () => setPreference('light') },
      { id: 'dark', title: 'Dark theme', group: 'Account', icon: Moon, keywords: ['appearance'], run: () => setPreference('dark') },
      { id: 'system', title: 'System theme', group: 'Account', icon: Monitor, keywords: ['appearance'], run: () => setPreference('system') },
      { id: 'sign-out', title: 'Sign out', group: 'Account', icon: LogOut, keywords: ['log out', 'logout'], run: () => void signOut() },
    ],
    [me.isAdmin, navigate, setPreference, signOut],
  )

  const matches = useMemo(
    () =>
      commands
        .map((c) => ({ c, score: commandFilter(c.title, search, c.keywords) }))
        .filter((m) => m.score > 0)
        .sort((a, b) => b.score - a.score)
        .map((m) => m.c),
    [commands, search],
  )
  const groups = [...new Set(commands.map((c) => c.group))]

  const item = (c: Command) => (
    <CommandItem
      key={c.id}
      value={c.id}
      onSelect={() => {
        onOpenChange(false)
        setSearch('')
        c.run()
      }}
    >
      <c.icon /> {c.title}
      {search && <span className="ml-auto text-xs text-muted-foreground">{c.group}</span>}
    </CommandItem>
  )

  return (
    <CommandDialog
      open={open}
      onOpenChange={(o) => {
        onOpenChange(o)
        if (!o) setSearch('')
      }}
      title="Search and commands"
      description="Go to a page or run a command"
      shouldFilter={false}
    >
      <CommandInput placeholder="Go to a page or run a command…" value={search} onValueChange={setSearch} />
      <CommandList>
        <CommandEmpty>Nothing matches.</CommandEmpty>
        {search ? (
          <CommandGroup heading="Results">{matches.map(item)}</CommandGroup>
        ) : (
          groups.map((g) => (
            <CommandGroup key={g} heading={g}>
              {commands.filter((c) => c.group === g).map(item)}
            </CommandGroup>
          ))
        )}
      </CommandList>
    </CommandDialog>
  )
}
