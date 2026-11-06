import { useMutation, useQueryClient } from '@tanstack/react-query'
import { FileArchive, FolderOpen, ShieldAlert, Upload } from 'lucide-react'
import { useRef, useState, type ReactNode } from 'react'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { toast } from '@/components/ui/toaster'
import { errorMessage } from '@/lib/api'
import { defaultAction, previewImport, runImport, sizeLabel, skillsQuery, type SkillImportAction, type SkillImportChoice, type SkillImportPreview, type SkillImportResult, type SkillPreview } from '@/lib/skills'
import { cn } from '@/lib/utils'

const shadowed: Record<string, string> = {
  group: 'a group’s',
  company: 'the company’s',
  builtin: 'Arena’s own',
  plugin: 'a plugin’s',
}

/** The choices a skill of the import offers: what is in its way decides which. */
function actionsFor(s: SkillPreview): { value: SkillImportAction; label: string }[] {
  if (s.problem) return [{ value: 'skip', label: 'Skip' }]
  if (s.clash) return [{ value: 'replace', label: 'Replace' }, { value: 'rename', label: 'Rename' }, { value: 'skip', label: 'Skip' }]
  if (s.nameProblem) return [{ value: 'rename', label: 'Rename' }, { value: 'skip', label: 'Skip' }]
  return [{ value: 'import', label: 'Import' }, { value: 'rename', label: 'Rename' }, { value: 'skip', label: 'Skip' }]
}

/**
 * Import skills (Skills → Import; Code Arena's Skills view, with its own look and bring):
 * a .zip of skill folders, a SKILL.md, or a folder, as Claude Code keeps them. A preview lists each skill with what it
 * brings and what is in its way: one of the person's own of its name (Replace, Rename or Skip), a name that is no slash
 * name (Rename), or why it cannot come in (the safeguards, the limits). Nothing is overwritten unless Replace was chosen.
 */
export function SkillImport({
  onClose,
  look: preview = previewImport,
  bring = runImport,
  done,
  intro,
  about = 'They become yours; every file goes through the safeguards first.',
}: {
  onClose: () => void
  look?: (files: File[]) => Promise<SkillImportPreview>
  bring?: (files: File[], choices: SkillImportChoice[]) => Promise<SkillImportResult>
  /** Told once they are imported (the list to read again); the Skills page's by default. */
  done?: () => Promise<void>
  /** Above the files' choice: Code Arena's choice of where they go. */
  intro?: ReactNode
  about?: string
}) {
  const queryClient = useQueryClient()
  const [files, setFiles] = useState<File[]>([])
  const [previewed, setPreview] = useState<SkillImportPreview | null>(null)
  const [actions, setActions] = useState<Record<string, SkillImportAction>>({})
  const [names, setNames] = useState<Record<string, string>>({})
  const [dragging, setDragging] = useState(false)
  const filePicker = useRef<HTMLInputElement>(null)
  const folderPicker = useRef<HTMLInputElement>(null)
  const look = useMutation({
    mutationFn: preview,
    onSuccess: (p) => {
      setPreview(p)
      setActions(Object.fromEntries(p.skills.map((s) => [s.folder, defaultAction(s)])))
      setNames(Object.fromEntries(p.skills.map((s) => [s.folder, s.suggested])))
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const run = useMutation({
    mutationFn: () => bring(files, (previewed?.skills ?? []).map((s) => ({ folder: s.folder, action: actions[s.folder] ?? 'skip', ...(actions[s.folder] === 'rename' ? { name: names[s.folder]?.trim() } : {}) }))),
    onSuccess: async (result) => {
      await (done ? done() : queryClient.invalidateQueries({ queryKey: skillsQuery.queryKey }))
      const n = result.imported.length
      if (n > 0) toast.success(`Imported ${n} skill${n === 1 ? '' : 's'}: ${result.imported.map((i) => '/' + i.name).join(', ')}.`)
      for (const s of result.skipped.filter((x) => actions[x.folder] !== 'skip')) toast.error(`/${s.name}: ${s.reason}`)
      if (n === 0 && result.skipped.every((x) => actions[x.folder] === 'skip')) toast.info('Nothing was imported.')
      onClose()
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const choose = (chosen: File[]) => {
    if (chosen.length === 0) return
    setFiles(chosen)
    setPreview(null)
    look.mutate(chosen)
  }
  const bringing = (previewed?.skills ?? []).filter((s) => actions[s.folder] && actions[s.folder] !== 'skip')
  const renamedBadly = bringing.some((s) => actions[s.folder] === 'rename' && !slashName.test(names[s.folder]?.trim() ?? ''))
  const newOnes = bringing.filter((s) => actions[s.folder] !== 'replace').length
  const overRoom = previewed?.room !== undefined && newOnes > previewed.room
  return (
    <>
      <DialogHeader>
        <DialogTitle>Import skills</DialogTitle>
        <DialogDescription>Skills as Claude Code keeps them: a .zip of skill folders, a SKILL.md, or a folder with a SKILL.md and the files beside it. {about}</DialogDescription>
      </DialogHeader>
      {intro}
      <input
        ref={filePicker}
        type="file"
        accept=".zip,.skill,.md,application/zip,text/markdown"
        className="sr-only"
        tabIndex={-1}
        aria-label="A .zip of skills or a SKILL.md"
        onChange={(e) => {
          choose([...(e.target.files ?? [])])
          e.target.value = ''
        }}
      />
      <input
        ref={folderPicker}
        type="file"
        multiple
        className="sr-only"
        tabIndex={-1}
        aria-label="A skill's folder"
        // A folder's files, each with its path in it: the browser's folder picker.
        {...{ webkitdirectory: '', directory: '' }}
        onChange={(e) => {
          choose([...(e.target.files ?? [])])
          e.target.value = ''
        }}
      />
      <div
        className={cn('grid place-items-center gap-3 rounded-lg border border-dashed p-6 text-center text-sm text-muted-foreground', dragging && 'border-primary bg-accent/50')}
        onDragOver={(e) => {
          e.preventDefault()
          setDragging(true)
        }}
        onDragLeave={() => setDragging(false)}
        onDrop={(e) => {
          e.preventDefault()
          setDragging(false)
          choose([...e.dataTransfer.files])
        }}
      >
        <Upload className="size-6" aria-hidden="true" />
        <p>{files.length > 0 ? `${files.length === 1 ? files[0]!.name : `${files.length} files`} · ${sizeLabel(files.reduce((n, f) => n + f.size, 0))}` : 'Drop a .zip or a SKILL.md here, or choose one.'}</p>
        <div className="flex flex-wrap justify-center gap-2">
          <Button type="button" variant="outline" size="sm" onClick={() => filePicker.current?.click()} disabled={look.isPending || run.isPending}>
            <FileArchive /> Choose a file
          </Button>
          <Button type="button" variant="outline" size="sm" onClick={() => folderPicker.current?.click()} disabled={look.isPending || run.isPending}>
            <FolderOpen /> Choose a folder
          </Button>
        </div>
        <p className="text-xs">Up to 5 MB and 50 skills; text files only, 200 KB each.</p>
      </div>
      {look.isPending && <output className="text-sm text-muted-foreground">Reading the skills…</output>}
      {previewed && (
        <div className="grid max-h-[50vh] gap-3 overflow-y-auto pe-1">
          {previewed.note && <p className="text-xs text-muted-foreground">{previewed.note}</p>}
          <ul className="grid gap-3" aria-label="Skills to import">
            {previewed.skills.map((s) => (
              <PreviewRow
                key={s.folder}
                skill={s}
                action={actions[s.folder] ?? 'skip'}
                name={names[s.folder] ?? s.suggested}
                onAction={(a) => setActions((x) => ({ ...x, [s.folder]: a }))}
                onName={(n) => setNames((x) => ({ ...x, [s.folder]: n.toLowerCase().replace(/\s+/g, '-') }))}
              />
            ))}
          </ul>
          {previewed.leftOut.length > 0 && (
            <details className="text-xs text-muted-foreground">
              <summary className="cursor-pointer">Left out of the upload ({previewed.leftOut.length})</summary>
              <ul className="mt-1 grid gap-0.5 font-mono [overflow-wrap:anywhere]">
                {previewed.leftOut.map((l) => (
                  <li key={l}>{l}</li>
                ))}
              </ul>
            </details>
          )}
          {overRoom && <Alert variant="destructive">You may have {previewed.room} more skills: skip some, or delete some of yours first.</Alert>}
        </div>
      )}
      <DialogFooter>
        <Button type="button" variant="outline" onClick={onClose}>
          Cancel
        </Button>
        <Button type="button" disabled={!previewed || bringing.length === 0 || renamedBadly || overRoom} loading={run.isPending} onClick={() => run.mutate()}>
          {bringing.length > 0 ? `Import ${bringing.length} skill${bringing.length === 1 ? '' : 's'}` : 'Import'}
        </Button>
      </DialogFooter>
    </>
  )
}

/** A skill's name as both Arena and Code Arena take it: lowercase letters, digits, - and _ (Arena's up to 40, checked there). */
const slashName = /^[a-z0-9][a-z0-9_-]{0,63}$/

function PreviewRow({ skill: s, action, name, onAction, onName }: { skill: SkillPreview; action: SkillImportAction; name: string; onAction: (a: SkillImportAction) => void; onName: (n: string) => void }) {
  const choices = actionsFor(s)
  const label = `/${s.name}`
  const nameOk = slashName.test(name.trim())
  return (
    <li className="grid gap-2 rounded-lg border p-3 text-sm" aria-label={label}>
      <div className="flex flex-wrap items-baseline gap-x-2 gap-y-1">
        <span className="font-mono [overflow-wrap:anywhere]">{label}</span>
        <span dir="auto" className="min-w-0 text-muted-foreground [overflow-wrap:anywhere]">
          {s.title}
        </span>
        <span className="ms-auto flex flex-wrap gap-1">
          {s.files.length > 0 && <Badge variant="outline" title={s.files.map((f) => f.path).join('\n')}>{`${s.files.length} file${s.files.length === 1 ? '' : 's'}`}</Badge>}
          {s.parameters > 0 && <Badge variant="outline">{`${s.parameters} parameter${s.parameters === 1 ? '' : 's'}`}</Badge>}
          <Badge variant="outline">{sizeLabel(s.bytes)}</Badge>
        </span>
      </div>
      {s.description && (
        <p dir="auto" className="text-muted-foreground [overflow-wrap:anywhere]">
          {s.description}
        </p>
      )}
      {s.problem ? (
        <p className="flex items-start gap-1.5 text-destructive [overflow-wrap:anywhere]">
          <ShieldAlert className="mt-0.5 size-4 shrink-0" aria-hidden="true" /> {s.problem}
        </p>
      ) : (
        <>
          {s.clash && (
            <p className="[overflow-wrap:anywhere]">
              {s.clash.origin ? `There is a /${s.name} in ${s.clash.origin} already` : `You have /${s.name} already (“${s.clash.title}”)`}: replace it, import this one under another name, or skip it.
            </p>
          )}
          {s.nameProblem && <p className="[overflow-wrap:anywhere]">{s.nameProblem} It comes in renamed.</p>}
          {!s.clash && s.shadows && <p className="text-muted-foreground">There is {shadowed[s.shadows] ?? 'another'} /{s.name}: yours will come first.</p>}
          {s.masked && <p className="text-muted-foreground">The safeguards masked part of it: it comes in masked.</p>}
          <div className="flex flex-wrap items-center gap-2">
            <div role="radiogroup" aria-label={`What to do with ${label}`} className="inline-flex rounded-md border p-0.5">
              {choices.map((c) => (
                <label
                  key={c.value}
                  className={cn(
                    'cursor-pointer rounded-sm px-2.5 py-1 text-xs has-focus-visible:ring-2 has-focus-visible:ring-ring',
                    action === c.value ? 'bg-primary text-primary-foreground' : 'text-muted-foreground hover:bg-accent hover:text-foreground',
                  )}
                >
                  <input type="radio" className="sr-only" name={`action-${s.folder}`} value={c.value} checked={action === c.value} onChange={() => onAction(c.value)} />
                  {c.label}
                </label>
              ))}
            </div>
            {action === 'rename' && (
              <Input
                value={name}
                onChange={(e) => onName(e.target.value)}
                aria-label={`New name for ${label}`}
                aria-invalid={!nameOk}
                className="h-8 w-48 font-mono text-xs"
              />
            )}
          </div>
        </>
      )}
      {s.leftOut.length > 0 && <p className="text-xs text-muted-foreground [overflow-wrap:anywhere]">Left out: {s.leftOut.join('; ')}</p>}
    </li>
  )
}
