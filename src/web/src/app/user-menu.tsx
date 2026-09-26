import { useQuery } from '@tanstack/react-query'
import { Columns2, LifeBuoy, LogOut, Maximize2, Monitor, Moon, Palette, RectangleHorizontal, Sun, UserRound } from 'lucide-react'
import { useNavigate } from 'react-router'
import { Avatar } from '@/components/ui/avatar'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuRadioGroup,
  DropdownMenuRadioItem,
  DropdownMenuSeparator,
  DropdownMenuSub,
  DropdownMenuSubContent,
  DropdownMenuSubTrigger,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'
import { infoQuery, supportHref, type Me } from '@/lib/api'
import { useTheme, type ThemePreference } from '@/lib/theme'
import { setWidth, useWidth, type WidthPreference } from '@/lib/width'
import { useSignOut } from './use-sign-out'

export function UserMenu({ me }: { me: Me }) {
  const navigate = useNavigate()
  const signOut = useSignOut()
  const { preference, setPreference } = useTheme()
  const width = useWidth()
  const support = useQuery(infoQuery).data?.supportContact
  const supportLink = support ? supportHref(support) : null
  return (
    <DropdownMenu>
      <DropdownMenuTrigger className="rounded-full outline-none focus-visible:ring-[3px] focus-visible:ring-ring" aria-label={`Account menu for ${me.displayName}`}>
        <Avatar name={me.displayName || me.userName} />
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end" className="w-60">
        <DropdownMenuLabel className="font-normal">
          <p className="truncate text-sm font-medium text-foreground">{me.displayName}</p>
          <p className="truncate text-xs text-muted-foreground">{me.email}</p>
        </DropdownMenuLabel>
        <DropdownMenuSeparator />
        <DropdownMenuItem onSelect={() => navigate('/account')}>
          <UserRound /> Your account
        </DropdownMenuItem>
        <DropdownMenuSub>
          <DropdownMenuSubTrigger>
            <Palette /> Theme
          </DropdownMenuSubTrigger>
          <DropdownMenuSubContent>
            <DropdownMenuRadioGroup value={preference} onValueChange={(v) => setPreference(v as ThemePreference)}>
              <DropdownMenuRadioItem value="light">
                <Sun /> Light
              </DropdownMenuRadioItem>
              <DropdownMenuRadioItem value="dark">
                <Moon /> Dark
              </DropdownMenuRadioItem>
              <DropdownMenuRadioItem value="system">
                <Monitor /> System
              </DropdownMenuRadioItem>
            </DropdownMenuRadioGroup>
          </DropdownMenuSubContent>
        </DropdownMenuSub>
        <DropdownMenuSub>
          <DropdownMenuSubTrigger>
            <RectangleHorizontal /> Width
          </DropdownMenuSubTrigger>
          <DropdownMenuSubContent>
            <DropdownMenuRadioGroup value={width} onValueChange={(v) => setWidth(v as WidthPreference)}>
              <DropdownMenuRadioItem value="comfortable">
                <Columns2 /> Comfortable
              </DropdownMenuRadioItem>
              <DropdownMenuRadioItem value="wide">
                <RectangleHorizontal /> Wide
              </DropdownMenuRadioItem>
              <DropdownMenuRadioItem value="full">
                <Maximize2 /> Full width
              </DropdownMenuRadioItem>
            </DropdownMenuRadioGroup>
          </DropdownMenuSubContent>
        </DropdownMenuSub>
        {support && (
          <DropdownMenuItem onSelect={() => supportLink && window.open(supportLink, '_blank', 'noopener')} disabled={!supportLink}>
            <LifeBuoy /> {supportLink ? 'Get help' : `Help: ${support}`}
          </DropdownMenuItem>
        )}
        <DropdownMenuSeparator />
        <DropdownMenuItem onSelect={() => void signOut()}>
          <LogOut /> Sign out
        </DropdownMenuItem>
      </DropdownMenuContent>
    </DropdownMenu>
  )
}
