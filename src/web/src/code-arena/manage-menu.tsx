import { useQueryClient } from '@tanstack/react-query'
import { Settings } from 'lucide-react'
import { toast } from '@/components/ui/toaster'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuRadioGroup,
  DropdownMenuRadioItem,
  DropdownMenuSeparator,
  DropdownMenuShortcut,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'
import { Tooltip } from '@/components/ui/tooltip'
import { errorMessage } from '@/lib/api'
import { cn } from '@/lib/utils'
import { changeSettings, stateQuery, type CodeState } from './api'
import { runCommand } from './commands'
import { useKeyFor } from './keyboard'

const toolModes = { direct: 'Direct', code: 'As code', both: 'Both' } as const

/**
 * The gear at the activity bar's foot (VS Code's Manage): Settings, Keyboard Shortcuts, the colour theme, Extensions and
 * the palette, one click away; and how the model is given its tools (direct, as code, both, or as Arena says for it).
 */
export function ManageMenu({ state, edge }: { state: CodeState; edge: 'left' | 'right' }) {
  const queryClient = useQueryClient()
  const keys = {
    settings: useKeyFor('workbench.action.openSettings'),
    keybindings: useKeyFor('workbench.action.openGlobalKeybindings'),
    theme: useKeyFor('workbench.action.selectTheme'),
    palette: useKeyFor('workbench.action.showCommands'),
  }
  const setToolMode = async (mode: 'direct' | 'code' | 'both' | null) => {
    try {
      queryClient.setQueryData(stateQuery.queryKey, await changeSettings({ toolMode: mode }))
    } catch (e) {
      toast.error(errorMessage(e))
    }
  }
  const arena = state.toolModeArena
  return (
    <DropdownMenu>
      <Tooltip content="Manage: settings, keys, theme" side={edge === 'left' ? 'right' : 'left'}>
        <DropdownMenuTrigger asChild>
          <button
            type="button"
            aria-label="Manage"
            className={cn(
              'relative grid size-12 place-items-center text-muted-foreground outline-none transition-colors hover:text-foreground focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-inset [&_svg]:size-5',
            )}
          >
            <Settings aria-hidden="true" />
          </button>
        </DropdownMenuTrigger>
      </Tooltip>
      <DropdownMenuContent side={edge === 'left' ? 'right' : 'left'} align="end" className="w-80 [&_[role=menuitem]]:whitespace-nowrap">
        <DropdownMenuItem onSelect={() => runCommand('workbench.action.openSettings')}>
          Settings {keys.settings && <DropdownMenuShortcut>{keys.settings}</DropdownMenuShortcut>}
        </DropdownMenuItem>
        <DropdownMenuItem onSelect={() => runCommand('workbench.action.openGlobalKeybindings')}>
          Keyboard Shortcuts {keys.keybindings && <DropdownMenuShortcut>{keys.keybindings}</DropdownMenuShortcut>}
        </DropdownMenuItem>
        <DropdownMenuItem onSelect={() => runCommand('workbench.action.selectTheme')}>
          Color Theme {keys.theme && <DropdownMenuShortcut>{keys.theme}</DropdownMenuShortcut>}
        </DropdownMenuItem>
        <DropdownMenuItem onSelect={() => runCommand('workbench.view.extensions')}>Extensions</DropdownMenuItem>
        <DropdownMenuItem onSelect={() => runCommand('workbench.action.showCommands')}>
          Command Palette {keys.palette && <DropdownMenuShortcut>{keys.palette}</DropdownMenuShortcut>}
        </DropdownMenuItem>
        <DropdownMenuSeparator />
        <DropdownMenuLabel className="text-xs font-medium text-muted-foreground">{`${state.model}'s tools`}</DropdownMenuLabel>
        <DropdownMenuRadioGroup value={state.toolModeSet ? (state.toolMode ?? 'both') : 'arena'} onValueChange={(v) => void setToolMode(v === 'arena' ? null : (v as 'direct' | 'code' | 'both'))}>
          <DropdownMenuRadioItem value="arena">As Arena says: {toolModes[arena ?? 'both'].toLowerCase()}</DropdownMenuRadioItem>
          <DropdownMenuRadioItem value="direct">Direct</DropdownMenuRadioItem>
          <DropdownMenuRadioItem value="code">As code</DropdownMenuRadioItem>
          <DropdownMenuRadioItem value="both">Both</DropdownMenuRadioItem>
        </DropdownMenuRadioGroup>
      </DropdownMenuContent>
    </DropdownMenu>
  )
}
