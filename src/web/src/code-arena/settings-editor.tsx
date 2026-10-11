import { useQueryClient } from '@tanstack/react-query'
import { Braces, Download, MoreHorizontal, Search } from 'lucide-react'
import { useState, type ReactNode } from 'react'
import { Alert } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { useConfirm } from '@/components/ui/confirm'
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuTrigger } from '@/components/ui/dropdown-menu'
import { toast } from '@/components/ui/toaster'
import { errorMessage } from '@/lib/api'
import { cn } from '@/lib/utils'
import { runCommand } from './commands'
import { userFile } from './editor-options'
import { useEditor } from './editor-state'
import { userFileQuery, vscodeUserFile, writeUserFile } from './ide-api'
import { parse, setProperty } from './jsonc'
import { useSettings, type Layer, type Target } from './settings'
import { settingDef, settingDefs, titleOf, type SettingDef } from './settings-schema'

/** What the search box asks: words, and VS Code's filters @modified, @lang:id and @id:key. */
function readQuery(q: string) {
  const words: string[] = []
  let modified = false
  let lang: string | undefined
  const ids: string[] = []
  for (const part of q.trim().split(/\s+/).filter(Boolean)) {
    if (part === '@modified') modified = true
    else if (part.startsWith('@lang:')) lang = part.slice(6) || undefined
    else if (part.startsWith('@id:')) ids.push(...part.slice(4).split(','))
    else words.push(part.toLowerCase())
  }
  return { words, modified, lang, ids }
}

/** Whether a layer sets a setting (for a language: in its block). */
const setIn = (layer: Layer, key: string, lang?: string) => (lang ? !!layer.languages[lang] && key in layer.languages[lang] : key in layer.values)

/**
 * The Settings editor (Ctrl+,), as VS Code's: every setting Code Arena reads, by category, searched by its words or with
 * @modified, @lang:python and @id:; the person's (User) or the project's (Workspace), a language's own with @lang; each
 * written into its settings.json as it changes, the file's comments kept.
 */
export default function SettingsEditor() {
  const settings = useSettings()
  const ed = useEditor()
  const confirm = useConfirm()
  const queryClient = useQueryClient()
  const [query, setQuery] = useState(ed.pageQuery)
  const [scope, setScope] = useState<Target>('user')
  const q = readQuery(query)
  const layer = scope === 'user' ? settings.user : settings.workspace
  const other = scope === 'user' ? settings.workspace : settings.user
  const shown = settingDefs.filter((d) => {
    if (q.lang && d.scope !== 'language') return false
    if (scope === 'workspace' && d.scope === 'application') return false
    if (q.ids.length > 0 && !q.ids.includes(d.key)) return false
    if (q.modified && !setIn(layer, d.key, q.lang)) return false
    const { group, name } = titleOf(d.key)
    const text = `${d.key} ${group} ${name} ${d.description} ${d.category}`.toLowerCase()
    return q.words.every((w) => text.includes(w))
  })
  const categories = [...new Set(shown.map((d) => d.category))]
  const unknown = Object.entries(layer.values).filter(([k]) => !settingDef.has(k))

  const write = (key: string, value: unknown) => void settings.update(key, value, scope, q.lang).catch((e: unknown) => toast.error(errorMessage(e)))

  /** VS Code's settings.json merged into the person's: what they set already stays. */
  const importVsCode = async () => {
    let theirs: string
    try {
      theirs = (await vscodeUserFile('settings.json')).text
    } catch (e) {
      toast.error(errorMessage(e))
      return
    }
    const { value } = parse(theirs)
    if (!value || typeof value !== 'object' || Array.isArray(value)) {
      toast.error("VS Code's settings.json is not an object of settings.")
      return
    }
    const entries = Object.entries(value as Record<string, unknown>).filter(([k]) => !(k in settings.user.values) && !(k.startsWith('[') && k.slice(1, -1) in settings.user.languages))
    const used = entries.filter(([k]) => settingDef.has(k) || k.startsWith('[')).length
    const ok = await confirm({
      title: "Bring over VS Code's settings?",
      description: `${entries.length} of them are not in your settings.json (${used} of those are used here). They are added to it; what you set already stays.`,
      confirm: 'Bring them over',
    })
    if (!ok) return
    try {
      const now = await queryClient.fetchQuery(userFileQuery('settings.json'))
      let text = now.text
      for (const [k, v] of entries) text = setProperty(text, [k], v)
      await writeUserFile('settings.json', text, now.version)
      await settings.reload()
      toast.success(`${entries.length} settings brought over.`)
    } catch (e) {
      toast.error(errorMessage(e))
    }
  }

  return (
    <section aria-label="Settings" className="absolute inset-0 flex flex-col overflow-hidden bg-background">
      <div className="grid gap-2 border-b px-4 pt-3 pb-2">
        <div className="flex items-center gap-2">
          <label className="flex h-8 min-w-0 flex-1 items-center gap-2 rounded-md border bg-background px-2 focus-within:ring-2 focus-within:ring-ring">
            <Search className="size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
            <input
              type="search"
              value={query}
              onChange={(e) => setQuery(e.target.value)}
              placeholder="Search settings (@modified, @lang:python, @id:editor.tabSize)"
              aria-label="Search settings"
              className="min-w-0 flex-1 bg-transparent text-sm outline-none"
            />
          </label>
          <Button size="sm" variant="ghost" onClick={() => void importVsCode()} title="Bring over VS Code's settings.json from this computer">
            <Download /> <span className="hidden md:inline">From VS Code</span>
          </Button>
          <Button size="sm" variant="ghost" onClick={() => (scope === 'user' ? void ed.open(userFile('settings.json')) : runCommand('workbench.action.openWorkspaceSettingsFile'))} title="Open Settings (JSON)" aria-label="Open Settings (JSON)">
            <Braces />
          </Button>
        </div>
        <div role="tablist" aria-label="Whose settings" className="flex gap-4 text-sm">
          {(['user', 'workspace'] as const).map((s) => (
            <button
              key={s}
              type="button"
              role="tab"
              aria-selected={scope === s}
              onClick={() => setScope(s)}
              className={cn('border-b-2 pb-1 outline-none focus-visible:ring-2 focus-visible:ring-ring', scope === s ? 'border-primary text-foreground' : 'border-transparent text-muted-foreground hover:text-foreground')}
            >
              {s === 'user' ? 'User' : 'Workspace'}
            </button>
          ))}
          {q.lang && <span className="text-muted-foreground">for {q.lang} only</span>}
        </div>
      </div>
      <div className="flex min-h-0 flex-1">
        <div className="hidden w-44 shrink-0 overflow-y-auto border-r p-2 text-sm md:block">
          {categories.map((c) => (
            <a key={c} href={`#settings-${c.replaceAll(' ', '-')}`} className="block rounded-sm px-2 py-1 text-muted-foreground hover:bg-accent hover:text-foreground">
              {c}
            </a>
          ))}
        </div>
        <div className="min-w-0 flex-1 overflow-y-auto px-4 py-3">
          {layer.problems.length > 0 && (
            <Alert variant="warning" title={`${scope === 'user' ? 'Your' : "The project's"} settings.json has values that are not taken`} className="mb-3">
              <ul className="list-disc pl-4">
                {layer.problems.map((p) => (
                  <li key={p}>{p}</li>
                ))}
              </ul>
            </Alert>
          )}
          {shown.length === 0 && <p className="py-8 text-center text-sm text-muted-foreground">No setting matches.</p>}
          {categories.map((c) => (
            <div key={c} id={`settings-${c.replaceAll(' ', '-')}`} className="mb-6">
              <h2 className="mb-2 text-base font-semibold">{c}</h2>
              {shown
                .filter((d) => d.category === c)
                .map((d) => (
                  <Row
                    key={d.key}
                    def={d}
                    value={settings.get(d.key, q.lang)}
                    modified={setIn(layer, d.key, q.lang)}
                    alsoIn={setIn(other, d.key, q.lang) ? (scope === 'user' ? 'Workspace' : 'User') : undefined}
                    onChange={(v) => write(d.key, v)}
                    onReset={() => write(d.key, undefined)}
                    onJson={() => (scope === 'user' ? void ed.open(userFile('settings.json')) : runCommand('workbench.action.openWorkspaceSettingsFile'))}
                  />
                ))}
            </div>
          ))}
          {unknown.length > 0 && !q.lang && (
            <div className="mb-6">
              <h2 className="mb-1 text-base font-semibold">Other settings in settings.json</h2>
              <p className="mb-2 text-sm text-muted-foreground">Code Arena does not use these (an extension of VS Code's, perhaps): they stay in the file.</p>
              <ul className="grid gap-1 font-mono text-xs">
                {unknown.map(([k, v]) => (
                  <li key={k} className="truncate">
                    {k}: {JSON.stringify(v)}
                  </li>
                ))}
              </ul>
            </div>
          )}
        </div>
      </div>
    </section>
  )
}

function Row({
  def,
  value,
  modified,
  alsoIn,
  onChange,
  onReset,
  onJson,
}: {
  def: SettingDef
  value: unknown
  modified: boolean
  alsoIn?: string
  onChange: (v: unknown) => void
  onReset: () => void
  onJson: () => void
}) {
  const { group, name } = titleOf(def.key)
  const id = `setting-${def.key}`
  let control: ReactNode
  if (def.type === 'boolean') {
    control = (
      <label className="flex items-start gap-2 text-sm">
        <input id={id} type="checkbox" checked={!!value} onChange={(e) => onChange(e.target.checked)} className="mt-0.5 size-4 accent-primary" />
        <span className="text-muted-foreground">{def.description}</span>
      </label>
    )
  } else if (def.enum) {
    control = (
      <select id={id} value={String(value)} onChange={(e) => onChange(e.target.value)} className="h-8 max-w-sm rounded-md border bg-background px-2 text-sm">
        {def.enum.map((v, i) => (
          <option key={v} value={v} title={def.enumDescriptions?.[i]}>
            {v === '\n' ? 'LF (\\n)' : v === '\r\n' ? 'CRLF (\\r\\n)' : v}
          </option>
        ))}
      </select>
    )
  } else if (def.type === 'number' || def.type === 'integer' || def.type === 'string') {
    control = <TextValue id={id} def={def} value={value} onChange={onChange} />
  } else {
    control = (
      <Button size="sm" variant="outline" onClick={onJson}>
        <Braces /> Edit in settings.json
      </Button>
    )
  }
  return (
    <div className={cn('group relative mb-1 grid gap-1 rounded-md py-2 pr-2 pl-4', modified && 'before:absolute before:inset-y-2 before:left-0 before:w-0.5 before:rounded before:bg-primary')} data-setting={def.key}>
      <div className="flex items-center gap-2">
        <label htmlFor={id} className="text-sm">
          {group && <span className="text-muted-foreground">{group}: </span>}
          <span className="font-semibold">{name}</span>
        </label>
        {modified && <span className="sr-only">(modified)</span>}
        {alsoIn && <span className="text-xs text-muted-foreground">Also modified in: {alsoIn}</span>}
        <DropdownMenu>
          <DropdownMenuTrigger asChild>
            <button type="button" aria-label={`More actions for ${def.key}`} className="ml-auto grid size-6 place-items-center rounded-sm text-muted-foreground opacity-0 outline-none group-hover:opacity-100 hover:bg-accent focus-visible:opacity-100 focus-visible:ring-2 focus-visible:ring-ring">
              <MoreHorizontal className="size-4" />
            </button>
          </DropdownMenuTrigger>
          <DropdownMenuContent align="end">
            <DropdownMenuItem disabled={!modified} onSelect={onReset}>
              Reset Setting
            </DropdownMenuItem>
            <DropdownMenuItem onSelect={() => void navigator.clipboard?.writeText(def.key)}>Copy Setting ID</DropdownMenuItem>
            <DropdownMenuItem onSelect={() => void navigator.clipboard?.writeText(`"${def.key}": ${JSON.stringify(value)}`)}>Copy Setting as JSON</DropdownMenuItem>
          </DropdownMenuContent>
        </DropdownMenu>
      </div>
      {def.type !== 'boolean' && <p className="text-sm text-muted-foreground">{def.description}</p>}
      {control}
      <p className="font-mono text-[0.6875rem] text-muted-foreground/70">{def.key}</p>
    </div>
  )
}

/** A number or a text, written when it is left or Enter is pressed (a number out of its bounds says so instead). */
function TextValue({ id, def, value, onChange }: { id: string; def: SettingDef; value: unknown; onChange: (v: unknown) => void }) {
  const [draft, setDraft] = useState<string | null>(null)
  const shown = draft ?? (value === undefined || value === null ? '' : String(value))
  const number = def.type === 'number' || def.type === 'integer'
  const parsed = number ? Number(shown) : shown
  const why =
    number && (shown.trim() === '' || Number.isNaN(parsed) || (def.type === 'integer' && !Number.isInteger(parsed)))
      ? `A ${def.type === 'integer' ? 'whole number' : 'number'}.`
      : number && ((def.minimum !== undefined && (parsed as number) < def.minimum) || (def.maximum !== undefined && (parsed as number) > def.maximum))
        ? `From ${def.minimum ?? '…'} to ${def.maximum ?? '…'}.`
        : null
  const commit = () => {
    if (draft === null) return
    if (!why && parsed !== value) onChange(parsed)
    if (!why) setDraft(null)
  }
  return (
    <div className="grid gap-1">
      <input
        id={id}
        type={number ? 'number' : 'text'}
        value={shown}
        min={def.minimum}
        max={def.maximum}
        onChange={(e) => setDraft(e.target.value)}
        onBlur={commit}
        onKeyDown={(e) => e.key === 'Enter' && commit()}
        aria-invalid={!!why}
        className={cn('h-8 max-w-sm rounded-md border bg-background px-2 text-sm outline-none focus-visible:ring-2 focus-visible:ring-ring', why && 'border-destructive')}
      />
      {why && draft !== null && <p className="text-xs text-destructive-ink">{why}</p>}
    </div>
  )
}
