import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Pencil, Plus, Search, SquareSlash, Trash2 } from 'lucide-react'
import { useState } from 'react'
import { PageHeader } from '@/components/app/page-header'
import { PageSkeleton, QueryError } from '@/components/app/query-state'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { useConfirm } from '@/components/ui/confirm'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { EmptyState } from '@/components/ui/empty-state'
import { Field } from '@/components/ui/field'
import { Input, Textarea } from '@/components/ui/input'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { matchPrompts, promptsQuery, sourceLabel, variableLabel, variablesOf, type PromptItem, type PromptLibrary, type PromptSharing } from '@/lib/prompts'

const sections: { source: PromptItem['source']; title: string; description: string }[] = [
  { source: 'mine', title: 'Yours', description: 'Yours to change; shared with your groups if you chose so.' },
  { source: 'group', title: 'Shared with you', description: 'From people in your groups.' },
  { source: 'company', title: 'For everyone', description: 'The company’s, kept by admins.' },
  { source: 'plugin', title: 'From plugins', description: 'They come and go with their plugin.' },
]

/** The prompt library: prompts with blanks, found with / in the chat; a person's own, their groups', the company's and plugins'. */
export function PromptsPage() {
  const queryClient = useQueryClient()
  const confirm = useConfirm()
  const library = useQuery(promptsQuery)
  const [editing, setEditing] = useState<PromptItem | 'new' | null>(null)
  const [search, setSearch] = useState('')
  const remove = useMutation({
    mutationFn: (p: PromptItem) => api(`/api/prompts/${p.id}`, { method: 'DELETE' }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: promptsQuery.queryKey }),
    onError: (e) => toast.error(errorMessage(e)),
  })
  if (library.isPending) return <PageSkeleton />
  if (library.error) return <QueryError error={library.error} retry={() => library.refetch()} />
  const shown = search.trim() ? matchPrompts(library.data.prompts, search.trim().replace(/^\//, '')) : library.data.prompts
  return (
    <>
      <PageHeader
        title="Prompts"
        description="Prompts you use often, with blanks to fill in each time: type / in the chat to find one. Keep them to yourself, share them with your groups, or, as an admin, with everyone."
        actions={
          <Button onClick={() => setEditing('new')}>
            <Plus /> New prompt
          </Button>
        }
      />
      {library.data.prompts.length === 0 ? (
        <EmptyState
          icon={SquareSlash}
          title="No prompts yet"
          action={
            <Button variant="outline" onClick={() => setEditing('new')}>
              <Plus /> New prompt
            </Button>
          }
        >
          Write what you ask often once, with blanks like {'{{file}}'}: in the chat, /its-name brings it back and asks for the blanks.
        </EmptyState>
      ) : (
        <div className="grid gap-8">
          <div className="relative max-w-sm">
            <Search className="pointer-events-none absolute top-1/2 left-2.5 size-4 -translate-y-1/2 text-muted-foreground" aria-hidden="true" />
            <Input className="ps-8" value={search} onChange={(e) => setSearch(e.target.value)} placeholder="Find by name or title" aria-label="Find a prompt" />
          </div>
          {sections.map((s) => {
            const list = shown.filter((p) => p.source === s.source)
            if (list.length === 0) return null
            return (
              <section key={s.source} aria-label={s.title} className="grid gap-3">
                <div>
                  <h2 className="text-base font-semibold">{s.title}</h2>
                  <p className="text-sm text-muted-foreground">{s.description}</p>
                </div>
                <div className="grid gap-4 xl:grid-cols-2">
                  {list.map((p) => (
                    <PromptCard
                      key={p.id}
                      prompt={p}
                      onEdit={() => setEditing(p)}
                      onDelete={async () => {
                        if (await confirm({ title: `Delete /${p.name}?`, description: p.sharing === 'Personal' ? undefined : 'Nobody it is shared with can use it any more.', confirm: 'Delete', destructive: true }))
                          remove.mutate(p)
                      }}
                    />
                  ))}
                </div>
              </section>
            )
          })}
          {shown.length === 0 && <p className="text-sm text-muted-foreground">No prompt has “{search}” in its name or title.</p>}
        </div>
      )}
      <Dialog open={editing !== null} onOpenChange={(o) => !o && setEditing(null)}>
        <DialogContent className="grid-cols-[minmax(0,1fr)] sm:max-w-2xl">
          {editing !== null && <PromptForm key={editing === 'new' ? 'new' : editing.id} saved={editing === 'new' ? null : editing} library={library.data} onClose={() => setEditing(null)} />}
        </DialogContent>
      </Dialog>
    </>
  )
}

function PromptCard({ prompt: p, onEdit, onDelete }: { prompt: PromptItem; onEdit: () => void; onDelete: () => void }) {
  return (
    <Card aria-label={`/${p.name}`}>
      <CardHeader>
        <CardTitle className="flex flex-wrap items-center gap-2 text-base">
          <span className="font-mono text-[0.9375rem]">/{p.name}</span>
          <span dir="auto" className="font-normal text-muted-foreground [overflow-wrap:anywhere]">
            {p.title}
          </span>
        </CardTitle>
        <CardDescription className="flex flex-wrap gap-1.5">
          <Badge variant={p.source === 'mine' ? 'default' : 'secondary'}>{sourceLabel(p)}</Badge>
          {p.source === 'group' && p.from && <Badge variant="outline">from {p.from}</Badge>}
        </CardDescription>
      </CardHeader>
      <CardContent className="grid gap-3 text-sm">
        <p dir="auto" className="line-clamp-4 whitespace-pre-wrap text-muted-foreground">
          {p.text}
        </p>
        {p.variables.length > 0 && (
          <p className="flex flex-wrap items-center gap-1.5 text-xs text-muted-foreground">
            Asks for
            {p.variables.map((v) => (
              <Badge key={v} variant="outline" className="font-mono">
                {v}
              </Badge>
            ))}
          </p>
        )}
        {p.canEdit && (
          <div className="flex flex-wrap gap-2">
            <Button variant="outline" size="sm" onClick={onEdit}>
              <Pencil /> Edit
            </Button>
            <Button variant="outline" size="sm" onClick={onDelete}>
              <Trash2 /> Delete
            </Button>
          </div>
        )}
      </CardContent>
    </Card>
  )
}

const sharings: { value: PromptSharing; label: string; hint: string }[] = [
  { value: 'Personal', label: 'Only me', hint: 'Nobody else sees it.' },
  { value: 'Groups', label: 'People in my groups', hint: 'They use it; only you change it.' },
  { value: 'Company', label: 'Everyone', hint: 'Every person here; any admin changes it.' },
]

function PromptForm({ saved, library, onClose }: { saved: PromptItem | null; library: PromptLibrary; onClose: () => void }) {
  const queryClient = useQueryClient()
  const [form, setForm] = useState({
    name: saved?.name ?? '',
    title: saved?.title ?? '',
    text: saved?.text ?? '',
    sharing: saved?.sharing ?? ('Personal' as PromptSharing),
    groups: saved?.groups.map((g) => g.id) ?? [],
  })
  const [error, setError] = useState<string | null>(null)
  const blanks = variablesOf(form.text)
  const save = useMutation({
    mutationFn: () => {
      const body = { ...form, groups: form.sharing === 'Groups' ? form.groups : [] }
      return saved ? api(`/api/prompts/${saved.id}`, { method: 'PUT', body }) : api('/api/prompts', { body })
    },
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: promptsQuery.queryKey })
      toast.success(saved ? 'Prompt saved.' : 'Prompt added.', { description: `Type /${form.name.replace(/^\//, '')} in the chat to use it.` })
      onClose()
    },
    onError: (e) => setError(errorMessage(e)),
  })
  return (
    <>
      <DialogHeader>
        <DialogTitle>{saved ? `Edit /${saved.name}` : 'New prompt'}</DialogTitle>
        <DialogDescription>In the chat, / and its name bring it into the message box, with a field for each blank.</DialogDescription>
      </DialogHeader>
      <form
        className="grid gap-4"
        onSubmit={(e) => {
          e.preventDefault()
          setError(null)
          save.mutate()
        }}
      >
        {error && <Alert variant="destructive">{error}</Alert>}
        <div className="grid gap-4 sm:grid-cols-[minmax(0,14rem)_minmax(0,1fr)]">
          <Field label="Slash name" hint="Lowercase letters, digits, - and _.">
            <Input required maxLength={41} autoComplete="off" className="font-mono" placeholder="review" value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} />
          </Field>
          <Field label="Title" hint="What it does, in a few words.">
            <Input required maxLength={100} autoComplete="off" placeholder="Review code" value={form.title} onChange={(e) => setForm({ ...form, title: e.target.value })} />
          </Field>
        </div>
        <Field label="Text" hint={blanks.length ? `Asks for: ${blanks.map(variableLabel).join(', ')}.` : 'Write {{name}} where something changes each time: Review {{code}} for {{focus}}.'}>
          <Textarea dir="auto" required maxLength={20000} className="min-h-40" value={form.text} onChange={(e) => setForm({ ...form, text: e.target.value })} />
        </Field>
        <fieldset className="grid gap-2">
          <legend className="mb-1 text-sm font-medium">Who uses it</legend>
          {sharings
            .filter((s) => s.value !== 'Company' || library.isAdmin)
            .map((s) => {
              const disabled = s.value === 'Groups' && library.groups.length === 0
              return (
                <label key={s.value} className="flex items-start gap-2 text-sm has-[:disabled]:opacity-60">
                  <input type="radio" name="sharing" className="mt-1 size-3.5 accent-primary" value={s.value} checked={form.sharing === s.value} disabled={disabled} onChange={() => setForm({ ...form, sharing: s.value })} />
                  <span className="grid gap-0.5">
                    {s.label}
                    <span className="text-xs text-muted-foreground">{disabled ? 'You are in no group yet.' : s.hint}</span>
                  </span>
                </label>
              )
            })}
          {form.sharing === 'Groups' && (
            <fieldset className="flex flex-wrap gap-1.5 ps-5">
              <legend className="sr-only">Groups</legend>
              {library.groups.map((g) => (
                <label key={g.id} className="flex cursor-pointer items-center gap-1.5 rounded-md border px-2.5 py-1.5 text-sm has-[:checked]:border-primary has-[:checked]:bg-primary/10 has-[:focus-visible]:ring-[3px] has-[:focus-visible]:ring-ring">
                  <input
                    type="checkbox"
                    className="size-3.5 accent-primary outline-none"
                    checked={form.groups.includes(g.id)}
                    onChange={(e) => setForm({ ...form, groups: e.target.checked ? [...form.groups, g.id] : form.groups.filter((x) => x !== g.id) })}
                  />
                  {g.name}
                </label>
              ))}
            </fieldset>
          )}
        </fieldset>
        <DialogFooter>
          <Button type="button" variant="outline" onClick={onClose}>
            Cancel
          </Button>
          <Button type="submit" loading={save.isPending}>
            {saved ? 'Save' : 'Add prompt'}
          </Button>
        </DialogFooter>
      </form>
    </>
  )
}
