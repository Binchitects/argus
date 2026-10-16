import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Blocks, ChevronDown, ChevronRight, Download, FileUp, RefreshCw, Trash2 } from 'lucide-react'
import { useRef, useState, type ReactNode } from 'react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { useConfirm } from '@/components/ui/confirm'
import { Switch } from '@/components/ui/switch'
import { toast } from '@/components/ui/toaster'
import { Tooltip } from '@/components/ui/tooltip'
import { errorMessage } from '@/lib/api'
import { cn } from '@/lib/utils'
import { runCommand } from './commands'
import {
  contributesSummary,
  enableExtension,
  extensionFileUrl,
  extensionsQuery,
  importExtension,
  installVsix,
  localExtensionsQuery,
  recommendedQuery,
  uninstallExtension,
  type ExtensionItem,
  type LocalExtension,
  type Recommendation,
} from './extensions-api'
import { PartHelp } from './help'

const action =
  'grid size-6 place-items-center rounded-sm text-muted-foreground outline-none hover:bg-accent hover:text-foreground focus-visible:ring-2 focus-visible:ring-ring disabled:opacity-50 [&_svg]:size-3.5'

/** A part of the view that folds, as VS Code's: its title, how many it holds, and what it holds. */
function Section({ title, count, children, open: initially = true }: { title: string; count?: number; children: ReactNode; open?: boolean }) {
  const [open, setOpen] = useState(initially)
  return (
    <div className="border-t first:border-t-0">
      <h3>
        <button
          type="button"
          aria-expanded={open}
          onClick={() => setOpen(!open)}
          className="flex h-[1.375rem] w-full min-w-0 items-center gap-1 pl-1 text-left text-[0.6875rem] font-semibold tracking-wider text-muted-foreground uppercase outline-none focus-visible:ring-1 focus-visible:ring-ring"
        >
          {open ? <ChevronDown className="size-3.5" aria-hidden="true" /> : <ChevronRight className="size-3.5" aria-hidden="true" />}
          <span className="min-w-0 flex-1 truncate">{title}</span>
          {count !== undefined && <span className="pr-2 tabular-nums">{count}</span>}
        </button>
      </h3>
      {open && <div className="pb-1">{children}</div>}
    </div>
  )
}


/** An extension's icon, or the view's own. */
function ExtensionIcon({ item }: { item: Pick<ExtensionItem, 'id' | 'icon' | 'version'> }) {
  const [failed, setFailed] = useState(false)
  if (item.icon && !failed) return <img src={extensionFileUrl(item.id, item.icon, item.version)} alt="" aria-hidden="true" onError={() => setFailed(true)} className="size-8 shrink-0 rounded-sm object-contain" />
  return (
    <span className="grid size-8 shrink-0 place-items-center rounded-sm bg-muted text-muted-foreground" aria-hidden="true">
      <Blocks className="size-4" />
    </span>
  )
}

/** One installed extension: what it is and brings, on or off, and what of it is not used here. */
function Installed({ item, busy, onEnable, onUninstall }: { item: ExtensionItem; busy: boolean; onEnable: (on: boolean) => void; onUninstall: () => void }) {
  const brings = contributesSummary(item.contributes)
  return (
    <li aria-label={item.displayName} className={cn('flex gap-2 px-3 py-1.5 hover:bg-accent/50', !item.enabled && 'opacity-70')}>
      <ExtensionIcon item={item} />
      <div className="min-w-0 flex-1">
        <div className="flex min-w-0 items-center gap-1.5">
          <span className="min-w-0 truncate font-medium" title={item.id}>
            {item.displayName}
          </span>
          <span className="shrink-0 text-xs text-muted-foreground">{item.version}</span>
          {!item.enabled && (
            <Badge variant="secondary" className="px-1 py-0 text-[0.625rem]">
              Disabled
            </Badge>
          )}
          <span className="flex-1" />
          <Tooltip content={item.enabled ? 'Disable' : 'Enable'}>
            <Switch aria-label={`${item.enabled ? 'Disable' : 'Enable'} ${item.displayName}`} checked={item.enabled} disabled={busy} onCheckedChange={onEnable} className="scale-75" />
          </Tooltip>
          <Tooltip content="Uninstall">
            <button type="button" aria-label={`Uninstall ${item.displayName}`} disabled={busy} onClick={onUninstall} className={action}>
              <Trash2 />
            </button>
          </Tooltip>
        </div>
        <p className="truncate text-xs text-muted-foreground" title={item.description ?? undefined}>
          {item.publisher}
          {item.description ? ` · ${item.description}` : ''}
        </p>
        {brings.length > 0 && <p className="text-xs">{brings.join(' · ')}</p>}
        {item.unused.length > 0 && (
          <details className="text-xs text-muted-foreground">
            <summary className="cursor-pointer select-none">Not used here ({item.unused.length})</summary>
            <ul className="list-disc pl-4">
              {item.unused.map((u) => (
                <li key={u}>{u}</li>
              ))}
            </ul>
          </details>
        )}
      </div>
    </li>
  )
}

const statusLabel: Record<Recommendation['status'], { text: string; variant: 'success' | 'secondary' | 'warning' | 'default' | 'outline' }> = {
  installed: { text: 'Installed', variant: 'success' },
  'built-in': { text: 'Built in', variant: 'default' },
  unsupported: { text: 'Not supported', variant: 'warning' },
  local: { text: 'On this computer', variant: 'secondary' },
  unknown: { text: 'Not here', variant: 'outline' },
}

/**
 * The Extensions view, as VS Code's (Ctrl+Shift+X): the extensions installed (on, off, uninstalled), those the project
 * recommends (.vscode/extensions.json) and where each stands here, VS Code's own on this computer to import, and a .vsix
 * to install. Only what extensions declare is used (themes, icons, snippets, languages, keys, setting defaults): none of
 * their code runs.
 */
export function ExtensionsView() {
  const queryClient = useQueryClient()
  const confirm = useConfirm()
  const list = useQuery(extensionsQuery)
  const recommended = useQuery(recommendedQuery)
  const local = useQuery(localExtensionsQuery)
  const [filter, setFilter] = useState('')
  const [busy, setBusy] = useState<string | null>(null)
  const file = useRef<HTMLInputElement>(null)

  const refresh = () => queryClient.invalidateQueries({ queryKey: ['code', 'extensions'] })
  /** Runs an action on an extension, one at a time: what it says when it fails, and the lists read again after. */
  const run = async (id: string, f: () => Promise<unknown>) => {
    setBusy(id)
    try {
      await f()
    } catch (e) {
      toast.error(errorMessage(e))
    } finally {
      setBusy(null)
      await refresh()
    }
  }
  const installed = (item: ExtensionItem) => {
    const themes = item.contributes.themes.length > 0
    const icons = item.contributes.iconThemes.length > 0
    toast.success(`${item.displayName} ${item.version} is installed.`, {
      description: contributesSummary(item.contributes).join(' · ') || undefined,
      ...(themes || icons ? { action: { label: themes ? 'Set Color Theme' : 'Set File Icon Theme', onClick: () => void runCommand(themes ? 'workbench.action.selectTheme' : 'workbench.action.selectIconTheme') } } : {}),
    })
  }
  const install = (f: File) => run('vsix', async () => installed(await installVsix(f)))
  const importOne = (id: string) => run(id, async () => installed(await importExtension(id)))
  const uninstall = async (item: ExtensionItem) => {
    const ok = await confirm({ title: `Uninstall ${item.displayName}?`, description: `Its files are taken away from Code Arena's data folder (VS Code's own copy, if any, stays).`, confirm: 'Uninstall', destructive: true })
    if (ok) await run(item.id, () => uninstallExtension(item.id))
  }

  const words = filter.toLowerCase().split(/\s+/).filter(Boolean)
  const matches = (...texts: (string | null | undefined)[]) => words.every((w) => texts.join(' ').toLowerCase().includes(w))
  const items = (list.data ?? []).filter((e) => matches(e.displayName, e.id, e.description, e.publisher))
  const recs = (recommended.data?.recommendations ?? []).filter((r) => matches(r.id, r.name, r.note))
  const vscode = (local.data ?? []).filter((e) => matches(e.displayName, e.id, e.description, e.publisher))

  return (
    <div className="flex h-full min-h-0 flex-col">
      <div className="flex h-9 shrink-0 items-center gap-1 pr-1.5 pl-3">
        <h2 className="min-w-0 flex-1 truncate text-[0.6875rem] font-semibold tracking-wider text-muted-foreground uppercase">Extensions</h2>
        <Tooltip content="Install from VSIX…">
          <button type="button" aria-label="Install from VSIX…" disabled={busy !== null} onClick={() => file.current?.click()} className={action}>
            <FileUp />
          </button>
        </Tooltip>
        <Tooltip content="Refresh">
          <button type="button" aria-label="Refresh the extensions" onClick={() => void refresh()} className={action}>
            <RefreshCw />
          </button>
        </Tooltip>
        <PartHelp part="extensions" />
        <input
          ref={file}
          type="file"
          accept=".vsix,application/zip,application/vsix"
          hidden
          data-vsix
          aria-label="A .vsix to install"
          onChange={(e) => {
            const f = e.target.files?.[0]
            e.target.value = ''
            if (f) void install(f)
          }}
        />
      </div>
      <div className="px-3 pb-2">
        <input
          type="search"
          value={filter}
          onChange={(e) => setFilter(e.target.value)}
          placeholder="Search extensions"
          aria-label="Search extensions"
          className="h-7 w-full rounded-md border bg-background px-2 text-xs outline-none focus-visible:ring-1 focus-visible:ring-ring"
        />
      </div>
      <div className="min-h-0 flex-1 overflow-auto pb-4 text-[0.8125rem]">
        <Section title="Installed" count={list.data?.length}>
          {list.isPending && <p className="px-3 text-xs text-muted-foreground">Reading the extensions…</p>}
          {list.error && <p className="px-3 text-xs text-muted-foreground">{errorMessage(list.error)}</p>}
          {list.data?.length === 0 && (
            <div className="grid gap-2 px-3 text-xs text-muted-foreground">
              <p>No extension is installed. Install a .vsix (Open VSX and the Marketplace give one for each extension), or import one VS Code has on this computer.</p>
              <Button size="sm" variant="outline" className="h-7 w-fit text-xs" disabled={busy !== null} onClick={() => file.current?.click()}>
                <FileUp /> Install from VSIX…
              </Button>
            </div>
          )}
          {items.length > 0 && (
            <ul aria-label="Installed extensions">
              {items.map((e) => (
                <Installed key={e.id} item={e} busy={busy === e.id} onEnable={(on) => void run(e.id, () => enableExtension(e.id, on))} onUninstall={() => void uninstall(e)} />
              ))}
            </ul>
          )}
        </Section>
        <Section title="Recommended" count={recommended.data?.recommendations.length}>
          {recommended.data && !recommended.data.file && <p className="px-3 text-xs text-muted-foreground">The project recommends none (.vscode/extensions.json).</p>}
          {recommended.data?.error && <p className="px-3 text-xs text-warning-ink">.vscode/extensions.json: {recommended.data.error}</p>}
          {recommended.error && <p className="px-3 text-xs text-muted-foreground">{errorMessage(recommended.error)}</p>}
          {recs.length > 0 && (
            <ul aria-label="Recommended extensions">
              {recs.map((r) => (
                <li key={r.id} aria-label={r.name ?? r.id} className="grid gap-0.5 px-3 py-1.5 hover:bg-accent/50">
                  <div className="flex min-w-0 items-center gap-1.5">
                    <span className="min-w-0 truncate font-medium" title={r.id}>
                      {r.name ?? r.id}
                    </span>
                    <Badge variant={statusLabel[r.status].variant} className="px-1 py-0 text-[0.625rem]">
                      {statusLabel[r.status].text}
                    </Badge>
                    <span className="flex-1" />
                    {r.status === 'local' && (
                      <Button size="sm" variant="outline" className="h-6 px-2 text-xs" disabled={busy !== null} onClick={() => void importOne(r.id)} aria-label={`Import ${r.name ?? r.id}`}>
                        <Download /> Import
                      </Button>
                    )}
                  </div>
                  <p className="text-xs text-muted-foreground">{r.note}</p>
                </li>
              ))}
            </ul>
          )}
        </Section>
        <Section title="VS Code on this computer" count={local.data?.length} open={false}>
          {local.isPending && <p className="px-3 text-xs text-muted-foreground">Looking for VS Code's extensions…</p>}
          {local.error && <p className="px-3 text-xs text-muted-foreground">{errorMessage(local.error)}</p>}
          {local.data?.length === 0 && <p className="px-3 text-xs text-muted-foreground">VS Code (or VSCodium, or Insiders) has no extension on this computer.</p>}
          {vscode.length > 0 && (
            <ul aria-label="VS Code's extensions on this computer">
              {vscode.map((e) => (
                <LocalRow key={e.id} item={e} busy={busy !== null} onImport={() => void importOne(e.id)} />
              ))}
            </ul>
          )}
        </Section>
      </div>
    </div>
  )
}

/** One of VS Code's extensions on this computer: import it, or why it cannot be. */
function LocalRow({ item, busy, onImport }: { item: LocalExtension; busy: boolean; onImport: () => void }) {
  const brings = contributesSummary(item.contributes)
  const same = item.installed === item.version
  return (
    <li aria-label={item.displayName} className={cn('grid gap-0.5 px-3 py-1.5 hover:bg-accent/50', !item.usable && 'opacity-70')}>
      <div className="flex min-w-0 items-center gap-1.5">
        <span className="min-w-0 truncate font-medium" title={`${item.id} · ${item.path}`}>
          {item.displayName}
        </span>
        <span className="shrink-0 text-xs text-muted-foreground">{item.version}</span>
        <span className="flex-1" />
        {same ? (
          <Badge variant="success" className="px-1 py-0 text-[0.625rem]">
            Installed
          </Badge>
        ) : (
          item.usable && (
            <Button size="sm" variant="outline" className="h-6 px-2 text-xs" disabled={busy} onClick={onImport} aria-label={`Import ${item.displayName}`}>
              <Download /> {item.installed ? 'Update' : 'Import'}
            </Button>
          )
        )}
      </div>
      <p className="truncate text-xs text-muted-foreground">
        {item.from}
        {item.installed && !same ? ` · ${item.installed} installed here` : ''}
      </p>
      {item.usable ? brings.length > 0 && <p className="text-xs">{brings.join(' · ')}</p> : item.problem && <p className="text-xs text-muted-foreground">{item.problem}</p>}
    </li>
  )
}
