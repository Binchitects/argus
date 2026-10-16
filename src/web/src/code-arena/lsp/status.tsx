import { LoaderCircle, ServerCog, ServerCrash, ServerOff, ShieldAlert } from 'lucide-react'
import { toast } from '@/components/ui/toaster'
import { cn } from '@/lib/utils'
import { runCommand } from '../commands'
import { useEditor } from '../editor-state'
import { isUserFile } from '../editor-options'
import { quickPick } from '../quick-pick'
import { languageServers, stateText, useLanguageServers } from './servers'

const icons = { starting: LoaderCircle, ready: ServerCog, failed: ServerCrash, restricted: ShieldAlert, taken: ServerOff, missing: ServerOff, off: ServerOff }

/**
 * The language server of the file shown, in the status bar: its name and its state (starting, ready and what it is
 * doing, failed, off until the folder is trusted…). Clicking it offers Restart Language Server and Show Language Server
 * Log, and what fits its state (Manage Workspace Trust, how to install it).
 */
export function LanguageServerStatus() {
  useLanguageServers()
  const { active } = useEditor()
  if (!active || active.kind !== 'file' || active.status !== 'ready' || isUserFile(active.path)) return null
  const s = languageServers.statusFor(active.languageId)
  if (!s) return null
  const said = stateText(s)
  const busy = s.state === 'starting' || (s.state === 'ready' && !!s.progress)
  const Icon = busy ? LoaderCircle : icons[s.state]
  const choose = async () => {
    const items = [
      ...(s.state === 'restricted' ? [{ id: 'trust', label: 'Manage Workspace Trust', description: 'its language servers run the folder\'s code' }] : []),
      ...(s.state === 'missing' && s.install ? [{ id: 'install', label: 'Copy how to install it', description: s.install }] : []),
      ...(s.state !== 'missing' ? [{ id: 'restart', label: 'Restart Language Server', description: s.name }] : []),
      { id: 'log', label: 'Show Language Server Log', description: s.name },
    ]
    const picked = await quickPick({ title: `${s.name}: ${said}`, placeholder: s.detail ?? 'Select an action', items })
    if (picked?.id === 'trust') runCommand('workbench.trust.manage')
    else if (picked?.id === 'restart') runCommand('codeArena.languageServer.restart', { id: s.id })
    else if (picked?.id === 'log') runCommand('codeArena.languageServer.showLog', { id: s.id })
    else if (picked?.id === 'install' && s.install)
      void navigator.clipboard?.writeText(s.install).then(
        () => toast.success('Copied.'),
        () => toast.error('The browser did not let the page copy.'),
      )
  }
  return (
    <button
      type="button"
      onClick={() => void choose()}
      title={`${s.name}${s.version ? ` ${s.version}` : ''}: ${said}${s.detail ? `. ${s.detail}` : ''}${s.install && s.state === 'missing' ? ` ${s.install}` : ''}`}
      aria-label={`Language server ${s.name}: ${said}`}
      className={cn(
        'hidden h-full min-w-0 shrink items-center gap-1 px-2 outline-none hover:bg-white/15 focus-visible:bg-white/20 focus-visible:ring-1 focus-visible:ring-white/70 focus-visible:ring-inset md:flex',
        (s.state === 'missing' || s.state === 'off' || s.state === 'restricted' || s.state === 'taken') && 'opacity-60',
        s.state === 'failed' && 'bg-warning text-black',
      )}
    >
      <Icon className={cn('size-3.5 shrink-0', busy && 'animate-spin motion-reduce:animate-none')} aria-hidden="true" />
      <span className="truncate">{s.state === 'ready' && s.progress ? `${s.name}: ${s.progress}` : s.name}</span>
    </button>
  )
}
