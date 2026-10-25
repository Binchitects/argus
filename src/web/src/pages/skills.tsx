import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Check, GraduationCap, History, Pencil, Plus, Search, Sparkles, SquareSlash, Trash2, Undo2, X } from 'lucide-react'
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
import { Label } from '@/components/ui/label'
import { Switch } from '@/components/ui/switch'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { applySkillChange, Arguments, dismissSkillChange, matchSkills, mustFill, skillsQuery, sourceLabel, undoSkillChange, variablesOf, type SkillChangeItem, type SkillItem, type SkillLibrary, type SkillParameter, type SkillSharing } from '@/lib/skills'

const sections: { source: SkillItem['source']; title: string; description: string }[] = [
  { source: 'mine', title: 'Yours', description: 'Yours to change; shared with your groups if you chose so.' },
  { source: 'group', title: 'Shared with you', description: 'From people in your groups.' },
  { source: 'company', title: 'For everyone', description: 'The company’s, kept by admins.' },
  { source: 'builtin', title: 'Arena’s own', description: 'They come with Arena. An admin turns one off for everyone; yours, a group’s or the company’s of the same name wins over it.' },
  { source: 'plugin', title: 'From plugins', description: 'They come and go with their plugin.' },
]

/**
 * Skills, as Claude Code's: instructions for a kind of work, with parameters, used with / in the chat (its fields filled in)
 * or by the model on its own when a question fits one; a person's own, their groups', the company's and plugins'.
 */
/**
 * What was learned from the person's work, waiting for them: a skill a chat worked out (Keep, or Not now), or a better
 * version of one they used there (Apply, or Not now), each with why.
 */
function Learned({ library }: { library: SkillLibrary }) {
  const queryClient = useQueryClient()
  const changed = () => queryClient.invalidateQueries({ queryKey: skillsQuery.queryKey })
  const apply = useMutation({ mutationFn: applySkillChange, onSuccess: changed, onError: (e) => toast.error(errorMessage(e)) })
  const dismiss = useMutation({ mutationFn: dismissSkillChange, onSuccess: changed, onError: (e) => toast.error(errorMessage(e)) })
  const waiting = (library.changes ?? []).filter((c) => c.proposed)
  if (waiting.length === 0) return null
  const skillOf = (c: SkillChangeItem) => library.skills.find((x) => x.id === c.skillId)
  return (
    <Card className="border-primary/40" aria-label="Learned from your work">
      <CardHeader>
        <CardTitle className="flex items-center gap-2 text-base">
          <GraduationCap className="size-4" aria-hidden="true" /> Learned from your work
        </CardTitle>
        <CardDescription>From your chats and Code Arena sessions that went quiet: a way of working worth doing again, or a better version of a skill that went wrong there.</CardDescription>
      </CardHeader>
      <CardContent>
        <ul className="grid gap-3" aria-label="Waiting for you">
          {waiting.map((c) => {
            const skill = skillOf(c)
            const name = skill ? `/${skill.name}` : 'a skill'
            return (
              <li key={c.id} className="grid gap-2 rounded-md border p-3 text-sm">
                <div className="flex flex-wrap items-center gap-2">
                  <Badge variant="secondary">{c.action === 'learned' ? 'New skill' : 'Better version'}</Badge>
                  <span className="font-mono">{name}</span>
                  {skill && <span className="text-muted-foreground">{skill.title}</span>}
                </div>
                {c.reason && <p className="text-muted-foreground">{c.reason}</p>}
                {skill?.description && c.action === 'learned' && <p>{skill.description}</p>}
                <details>
                  <summary className="cursor-pointer text-xs text-muted-foreground">{c.action === 'learned' ? 'Its instructions' : 'The new instructions, and the old'}</summary>
                  <p className="mt-2 font-mono text-xs whitespace-pre-wrap [overflow-wrap:anywhere]">{c.after}</p>
                  {c.before && <p className="mt-2 font-mono text-xs whitespace-pre-wrap text-muted-foreground line-through [overflow-wrap:anywhere]">{c.before}</p>}
                </details>
                <div className="flex flex-wrap gap-2">
                  <Button size="sm" disabled={apply.isPending} onClick={() => apply.mutate(c.id)} aria-label={`${c.action === 'learned' ? 'Keep' : 'Apply'} ${name}`}>
                    <Check /> {c.action === 'learned' ? 'Keep' : 'Apply'}
                  </Button>
                  <Button size="sm" variant="ghost" disabled={dismiss.isPending} onClick={() => dismiss.mutate(c.id)} aria-label={`Not now: ${name}`}>
                    Not now
                  </Button>
                </div>
              </li>
            )
          })}
        </ul>
      </CardContent>
    </Card>
  )
}

export function SkillsPage() {
  const queryClient = useQueryClient()
  const confirm = useConfirm()
  const library = useQuery(skillsQuery)
  const [editing, setEditing] = useState<SkillItem | 'new' | null>(null)
  const [search, setSearch] = useState('')
  const remove = useMutation({
    mutationFn: (p: SkillItem) => api(`/api/skills/${p.id}`, { method: 'DELETE' }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: skillsQuery.queryKey }),
    onError: (e) => toast.error(errorMessage(e)),
  })
  const turn = useMutation({
    mutationFn: (p: SkillItem) => api(`/api/skills/${p.id}/off`, { method: 'PUT', body: { off: !p.off } }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: skillsQuery.queryKey }),
    onError: (e) => toast.error(errorMessage(e)),
  })
  if (library.isPending) return <PageSkeleton />
  if (library.error) return <QueryError error={library.error} retry={() => library.refetch()} />
  const kept = library.data.skills.filter((p) => !p.proposed)
  const shown = search.trim() ? matchSkills(kept, search.trim().replace(/^\//, '')) : kept
  return (
    <>
      <PageHeader
        title="Skills"
        description="Instructions for work you ask often, with parameters filled in each time: type / in the chat to use one, or let the model use it on its own when a question fits what it is for. Keep them to yourself, share them with your groups, or, as an admin, with everyone."
        actions={
          <Button onClick={() => setEditing('new')}>
            <Plus /> New skill
          </Button>
        }
      />
      <div className="mb-8 empty:hidden">
        <Learned library={library.data} />
      </div>
      {kept.length === 0 ? (
        <EmptyState
          icon={SquareSlash}
          title="No skills yet"
          action={
            <Button variant="outline" onClick={() => setEditing('new')}>
              <Plus /> New skill
            </Button>
          }
        >
          Write how you want a kind of work done once, with parameters like {'{{file}}'}: in the chat, /its-name asks for them and sends the skill; with a description, the model uses it on its own when it fits.
        </EmptyState>
      ) : (
        <div className="grid gap-8">
          <div className="relative max-w-sm">
            <Search className="pointer-events-none absolute top-1/2 left-2.5 size-4 -translate-y-1/2 text-muted-foreground" aria-hidden="true" />
            <Input className="ps-8" value={search} onChange={(e) => setSearch(e.target.value)} placeholder="Find by name, title or description" aria-label="Find a skill" />
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
                    <SkillCard
                      key={p.id}
                      skill={p}
                      history={(library.data.changes ?? []).filter((c) => c.skillId === p.id && c.action === 'improved' && c.appliedAt)}
                      onTurn={library.data.isAdmin && p.source === 'builtin' ? () => turn.mutate(p) : undefined}
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
          {shown.length === 0 && <p className="text-sm text-muted-foreground">No skill has “{search}” in its name, title or description.</p>}
        </div>
      )}
      <Dialog open={editing !== null} onOpenChange={(o) => !o && setEditing(null)}>
        <DialogContent className="grid-cols-[minmax(0,1fr)] sm:max-w-2xl">
          {editing !== null && <SkillForm key={editing === 'new' ? 'new' : editing.id} saved={editing === 'new' ? null : editing} library={library.data} onClose={() => setEditing(null)} />}
        </DialogContent>
      </Dialog>
    </>
  )
}

function SkillCard({ skill: p, history, onEdit, onDelete, onTurn }: { skill: SkillItem; history: SkillChangeItem[]; onEdit: () => void; onDelete: () => void; onTurn?: () => void }) {
  const queryClient = useQueryClient()
  const undo = useMutation({
    mutationFn: undoSkillChange,
    onSuccess: () => queryClient.invalidateQueries({ queryKey: skillsQuery.queryKey }),
    onError: (e) => toast.error(errorMessage(e)),
  })
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
          {p.off && <Badge variant="destructive">Off for everyone</Badge>}
          {p.learned && (
            <Badge variant="outline">
              <GraduationCap aria-hidden="true" /> Learned
            </Badge>
          )}
          {(p.version ?? 1) > 1 && <Badge variant="outline">v{p.version}</Badge>}
          {p.autoUse && p.description ? (
            <Badge variant="outline">
              <Sparkles aria-hidden="true" /> Used on its own
            </Badge>
          ) : (
            <Badge variant="outline">/ only</Badge>
          )}
        </CardDescription>
      </CardHeader>
      <CardContent className="grid gap-3 text-sm">
        {p.description && (
          <p dir="auto" className="[overflow-wrap:anywhere]">
            {p.description}
          </p>
        )}
        <p dir="auto" className="line-clamp-3 font-mono text-xs whitespace-pre-wrap text-muted-foreground [overflow-wrap:anywhere]">
          {p.instructions}
        </p>
        {p.parameters.length > 0 && (
          <p className="flex flex-wrap items-center gap-1.5 text-xs text-muted-foreground">
            Asks for
            {p.parameters.map((v) => (
              <Badge key={v.name} variant="outline" className="font-mono" title={v.description ?? undefined}>
                {v.name}
                {!mustFill(v) && '?'}
              </Badge>
            ))}
          </p>
        )}
        {history.length > 0 && (
          <details className="text-xs">
            <summary className="flex cursor-pointer items-center gap-1 text-muted-foreground">
              <History className="size-3.5" aria-hidden="true" /> How it got better ({history.length})
            </summary>
            <ul className="mt-2 grid gap-2" aria-label={`How /${p.name} got better`}>
              {history.map((c) => (
                <li key={c.id} className="flex flex-wrap items-start gap-2">
                  <span className="min-w-0 flex-1 [overflow-wrap:anywhere]">{c.reason || 'A better version'}</span>
                  {c.undoneAt ? (
                    <span className="text-muted-foreground">Undone</span>
                  ) : (
                    <Button size="sm" variant="ghost" className="h-6" disabled={undo.isPending} onClick={() => undo.mutate(c.id)} aria-label={`Undo: ${c.reason || 'a better version'}`}>
                      <Undo2 /> Undo
                    </Button>
                  )}
                </li>
              ))}
            </ul>
          </details>
        )}
        {onTurn && (
          <div>
            <Button variant="outline" size="sm" onClick={onTurn}>
              {p.off ? 'Turn on for everyone' : 'Turn off for everyone'}
            </Button>
          </div>
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

const sharings: { value: SkillSharing; label: string; hint: string }[] = [
  { value: 'Personal', label: 'Only me', hint: 'Nobody else sees it.' },
  { value: 'Groups', label: 'People in my groups', hint: 'They use it; only you change it.' },
  { value: 'Company', label: 'Everyone', hint: 'Every person here; any admin changes it.' },
]

/** The parameters with every {{variable}} of the instructions among them (a new one required), in the order they come. */
function withVariables(parameters: SkillParameter[], instructions: string): SkillParameter[] {
  const added = variablesOf(instructions).filter((v) => !parameters.some((p) => p.name === v))
  return [...parameters, ...added.map((name) => ({ name, description: '', required: true, default: '' }))]
}

function SkillForm({ saved, library, onClose }: { saved: SkillItem | null; library: SkillLibrary; onClose: () => void }) {
  const queryClient = useQueryClient()
  const [form, setForm] = useState({
    name: saved?.name ?? '',
    title: saved?.title ?? '',
    description: saved?.description ?? '',
    instructions: saved?.instructions ?? '',
    autoUse: saved?.autoUse ?? true,
    sharing: saved?.sharing ?? ('Personal' as SkillSharing),
    groups: saved?.groups.map((g) => g.id) ?? [],
  })
  const [parameters, setParameters] = useState<SkillParameter[]>(saved?.parameters.map((p) => ({ ...p, description: p.description ?? '', default: p.default ?? '' })) ?? [])
  const [error, setError] = useState<string | null>(null)
  const unused = parameters.filter((p) => p.name && !variablesOf(form.instructions).includes(p.name)).map((p) => p.name)
  const setParameter = (i: number, change: Partial<SkillParameter>) => setParameters((list) => list.map((p, j) => (j === i ? { ...p, ...change } : p)))
  const save = useMutation({
    mutationFn: () => {
      const body = {
        ...form,
        groups: form.sharing === 'Groups' ? form.groups : [],
        parameters: withVariables(parameters, form.instructions)
          .filter((p) => p.name.trim())
          .map((p) => ({ name: p.name.trim(), description: p.description?.trim() || null, required: p.required, default: p.default?.trim() || null })),
      }
      return saved ? api(`/api/skills/${saved.id}`, { method: 'PUT', body }) : api('/api/skills', { body })
    },
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: skillsQuery.queryKey })
      toast.success(saved ? 'Skill saved.' : 'Skill added.', { description: `Type /${form.name.replace(/^\//, '')} in the chat to use it.` })
      onClose()
    },
    onError: (e) => setError(errorMessage(e)),
  })
  return (
    <>
      <DialogHeader>
        <DialogTitle>{saved ? `Edit /${saved.name}` : 'New skill'}</DialogTitle>
        <DialogDescription>In the chat, / and its name ask for its parameters, and the skill goes with your message: the model follows its instructions.</DialogDescription>
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
        <Field label="When to use it" hint="What it is for, as the model reads it to choose it: “Review a merge request for security and tests.”">
          <Textarea dir="auto" maxLength={1000} className="min-h-16" value={form.description} onChange={(e) => setForm({ ...form, description: e.target.value })} />
        </Field>
        <Field
          label="Instructions"
          hint={`How to do the work. Write {{name}} where a parameter goes, and ${Arguments} where what is typed after the fields goes (else it comes after the instructions).`}
        >
          <Textarea
            dir="auto"
            required
            maxLength={20000}
            className="min-h-40 font-mono text-[0.8125rem]"
            value={form.instructions}
            onChange={(e) => setForm({ ...form, instructions: e.target.value })}
            onBlur={() => setParameters((list) => withVariables(list, form.instructions))}
          />
        </Field>
        <fieldset className="grid gap-2">
          <legend className="mb-1 text-sm font-medium">Parameters</legend>
          {parameters.length === 0 && <p className="text-xs text-muted-foreground">None: a {'{{name}}'} in the instructions adds one.</p>}
          <ul className="grid gap-2" aria-label="Parameters">
            {parameters.map((p, i) => (
              <li key={i} aria-label={p.name || 'New parameter'} className="grid gap-2 rounded-lg border p-2.5 sm:grid-cols-[minmax(0,9rem)_minmax(0,1fr)_minmax(0,8rem)_auto_auto] sm:items-end">
                <Field label="Name">
                  <Input required maxLength={40} autoComplete="off" className="font-mono" value={p.name} onChange={(e) => setParameter(i, { name: e.target.value })} />
                </Field>
                <Field label="What it is">
                  <Input maxLength={300} autoComplete="off" placeholder="The file’s path" value={p.description ?? ''} onChange={(e) => setParameter(i, { description: e.target.value })} />
                </Field>
                <Field label="Default">
                  <Input maxLength={2000} autoComplete="off" value={p.default ?? ''} onChange={(e) => setParameter(i, { default: e.target.value })} />
                </Field>
                <Label className="flex h-9 items-center gap-1.5 text-sm font-normal">
                  <input type="checkbox" className="size-3.5 accent-primary" checked={p.required} onChange={(e) => setParameter(i, { required: e.target.checked })} />
                  Required
                </Label>
                <Button type="button" variant="ghost" size="icon-sm" className="justify-self-end" aria-label={`Remove ${p.name || 'parameter'}`} onClick={() => setParameters((list) => list.filter((_, j) => j !== i))}>
                  <X />
                </Button>
              </li>
            ))}
          </ul>
          {unused.length > 0 && <p className="text-xs text-muted-foreground">Not in the instructions: {unused.map((n) => `{{${n}}}`).join(', ')}. The model reads its value anyway.</p>}
          <Button type="button" variant="outline" size="sm" className="justify-self-start" onClick={() => setParameters((list) => [...list, { name: '', description: '', required: true, default: '' }])}>
            <Plus /> Add a parameter
          </Button>
        </fieldset>
        <div className="flex items-start gap-2">
          <Switch id="skill-auto" checked={form.autoUse} onCheckedChange={(autoUse) => setForm({ ...form, autoUse })} />
          <div className="grid gap-0.5">
            <Label htmlFor="skill-auto">The model uses it on its own</Label>
            <p className="text-xs text-muted-foreground">
              {form.description.trim() ? 'When a question fits what it is for (the Skills tool), its parameters taken from the question.' : 'Once it says when to use it.'} Off: only with /{form.name.replace(/^\//, '') || 'its-name'}.
            </p>
          </div>
        </div>
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
            {saved ? 'Save' : 'Add skill'}
          </Button>
        </DialogFooter>
      </form>
    </>
  )
}
