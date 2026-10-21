import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Copy, KeyRound, Plus, RefreshCw, Trash2 } from 'lucide-react'
import { useId, useState } from 'react'
import { QueryError } from '@/components/app/query-state'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Checkbox } from '@/components/ui/checkbox'
import { useConfirm } from '@/components/ui/confirm'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Skeleton } from '@/components/ui/skeleton'
import { Switch } from '@/components/ui/switch'
import { toast } from '@/components/ui/toaster'
import { errorMessage } from '@/lib/api'
import { listQuery } from '../chat/api'
import { addRule, changeRule, checkRule, newRuleSecret, removeRule, rulesQuery, type ReviewRule } from './api'

const NO_ROOM = 'none'

/** What a rule does, in a line. */
function described(r: ReviewRule) {
  const parts: string[] = []
  if (r.mergeRequests) {
    parts.push(`Merge requests${r.targetBranches.length ? ` into ${r.targetBranches.join(', ')}` : ''}${r.skipDrafts ? ', not drafts' : ''}`)
    parts.push(r.checkMinutes > 0 ? `looked at every ${r.checkMinutes} min` : 'by the webhook only')
  }
  if (r.branch && r.cron) parts.push(`${r.branch} over ${r.base} at "${r.cron}" (${r.timeZone})`)
  return parts.join(' · ') || 'Nothing to review'
}

/** The rule's webhook address and its secret, shown once, with where they go in GitLab. */
function Secret({ url, secret, onClose }: { url: string; secret: string; onClose: () => void }) {
  const copy = (text: string, what: string) => void navigator.clipboard.writeText(text).then(() => toast.success(`${what} copied`))
  return (
    <section className="grid gap-2 rounded-md border bg-muted/40 p-3 text-sm" aria-label="The webhook">
      <p>In GitLab, the project's Settings → Webhooks: this URL, this secret token, and Merge request events. The secret is shown once.</p>
      {[
        ['URL', url],
        ['Secret token', secret],
      ].map(([label, value]) => (
        <div key={label} className="flex items-center gap-2">
          <Input readOnly value={value} aria-label={`Webhook ${label}`} className="h-8 font-mono text-xs" onFocus={(e) => e.target.select()} />
          <Button size="icon-sm" variant="ghost" aria-label={`Copy the ${label.toLowerCase()}`} onClick={() => copy(value, label)}>
            <Copy />
          </Button>
        </div>
      ))}
      <div>
        <Button size="sm" variant="outline" onClick={onClose}>
          Done
        </Button>
      </div>
    </section>
  )
}

/**
 * Code → Automatic reviews: a project's merge requests reviewed as they open and get new commits (GitLab's webhook to the
 * rule's address, and a look at the open ones every few minutes), and a scheduled review of a branch. Read with your own
 * access, at your cost; each finished review can go to a room.
 */
export function AutoReviews() {
  const queryClient = useQueryClient()
  const confirm = useConfirm()
  const rules = useQuery(rulesQuery)
  const chats = useQuery(listQuery(''))
  const rooms = (chats.data ?? []).filter((c) => c.isRoom)
  const formId = useId()
  const [adding, setAdding] = useState(false)
  const [project, setProject] = useState('')
  const [targets, setTargets] = useState('')
  const [skipDrafts, setSkipDrafts] = useState(true)
  const [minutes, setMinutes] = useState('15')
  const [room, setRoom] = useState(NO_ROOM)
  const [branch, setBranch] = useState('')
  const [base, setBase] = useState('')
  const [cron, setCron] = useState('')
  const [shown, setShown] = useState<{ url: string; secret: string } | null>(null)
  const [error, setError] = useState<string | null>(null)
  const refresh = () => queryClient.invalidateQueries({ queryKey: ['code'] })

  const add = useMutation({
    mutationFn: () =>
      addRule({
        project: project.trim(),
        targetBranches: targets.split(',').map((t) => t.trim()).filter(Boolean),
        skipDrafts,
        checkMinutes: Number(minutes) || 0,
        ...(room !== NO_ROOM && { roomId: room }),
        ...(branch.trim() && { branch: branch.trim(), base: base.trim(), cron: cron.trim(), timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone }),
      }),
    onSuccess: (r) => {
      setError(null)
      setAdding(false)
      setProject('')
      setShown({ url: r.rule.hookUrl, secret: r.secret })
      void refresh()
    },
    onError: (e) => setError(errorMessage(e)),
  })
  const change = useMutation({
    mutationFn: ({ id, enabled }: { id: string; enabled: boolean }) => changeRule(id, { enabled }),
    onSuccess: () => void refresh(),
    onError: (e) => toast.error(errorMessage(e)),
  })
  const look = useMutation({
    mutationFn: (id: string) => checkRule(id),
    onSuccess: (r) => {
      toast.success('Looked at the open merge requests', { description: r.said.join(' · ') || 'None are open.' })
      void refresh()
    },
    onError: (e) => toast.error(errorMessage(e)),
  })

  return (
    <Card className="mt-8">
      <CardHeader>
        <CardTitle>Automatic reviews</CardTitle>
        <CardDescription>A project's merge requests reviewed as they open and get new commits, and a branch on a schedule. Read with your access, at your cost.</CardDescription>
      </CardHeader>
      <CardContent className="grid gap-4">
        {shown && <Secret url={shown.url} secret={shown.secret} onClose={() => setShown(null)} />}
        {rules.error ? (
          <QueryError error={rules.error} retry={() => rules.refetch()} />
        ) : rules.isPending ? (
          <Skeleton className="h-16" />
        ) : rules.data.length === 0 ? (
          <p className="text-sm text-muted-foreground">No project reviews itself yet.</p>
        ) : (
          <ul className="grid gap-2" aria-label="Automatic reviews">
            {rules.data.map((r) => (
              <li key={r.id} className="grid gap-2 rounded-md border px-3 py-2 text-sm">
                <div className="flex flex-wrap items-center gap-2">
                  <Switch checked={r.enabled} aria-label={`${r.project}: on`} onCheckedChange={(enabled) => change.mutate({ id: r.id, enabled })} />
                  <span className="min-w-0 flex-1">
                    <span className="block truncate font-medium">{r.project}</span>
                    <span className="block text-xs text-muted-foreground">{described(r)}</span>
                  </span>
                  <Badge variant="outline">
                    {r.today} of {r.mostPerDay} today
                  </Badge>
                  {r.mergeRequests && (
                    <Button size="sm" variant="ghost" onClick={() => look.mutate(r.id)} loading={look.isPending && look.variables === r.id}>
                      <RefreshCw /> Look now
                    </Button>
                  )}
                  <Button
                    size="icon-sm"
                    variant="ghost"
                    aria-label={`New webhook secret for ${r.project}`}
                    onClick={async () => {
                      if (await confirm({ title: 'Make a new secret?', description: 'The current secret stops working at once: set the new one in GitLab.', confirm: 'Make a new secret', destructive: true })) {
                        newRuleSecret(r.id).then((s) => setShown({ url: r.hookUrl, secret: s.secret }), (e: unknown) => toast.error(errorMessage(e)))
                      }
                    }}
                  >
                    <KeyRound />
                  </Button>
                  <Button
                    size="icon-sm"
                    variant="ghost"
                    aria-label={`Stop reviewing ${r.project}`}
                    onClick={async () => {
                      if (await confirm({ title: `Stop reviewing ${r.project}?`, description: 'Reviews waiting their turn go too; those done stay.', confirm: 'Stop', destructive: true })) {
                        removeRule(r.id).then(() => void refresh(), (e: unknown) => toast.error(errorMessage(e)))
                      }
                    }}
                  >
                    <Trash2 />
                  </Button>
                </div>
                {r.lastError && <p className="text-xs text-destructive">{r.lastError}</p>}
              </li>
            ))}
          </ul>
        )}
        {adding ? (
          <form
            id={formId}
            className="grid gap-3 rounded-md border p-3"
            aria-label="Add automatic reviews"
            onSubmit={(e) => {
              e.preventDefault()
              add.mutate()
            }}
          >
            {error && <Alert variant="destructive">{error}</Alert>}
            <Field label="Project">
              <Input value={project} onChange={(e) => setProject(e.target.value)} placeholder="group/project" />
            </Field>
            <div className="grid gap-3 sm:grid-cols-2">
              <Field label="Into branches" hint="Comma-separated; empty: any.">
                <Input value={targets} onChange={(e) => setTargets(e.target.value)} placeholder="main, release" />
              </Field>
              <Field label="Look every (minutes)" hint="0: only when GitLab's webhook says.">
                <Input inputMode="numeric" value={minutes} onChange={(e) => setMinutes(e.target.value)} />
              </Field>
            </div>
            <div className="flex items-center gap-2">
              <Checkbox id={`${formId}-drafts`} checked={skipDrafts} onCheckedChange={(on) => setSkipDrafts(on === true)} />
              <label htmlFor={`${formId}-drafts`}>Drafts wait until they are ready</label>
            </div>
            <Field label="Post each review in">
              <Select value={room} onValueChange={setRoom}>
                <SelectTrigger>
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value={NO_ROOM}>No room: tell only me</SelectItem>
                  {rooms.map((c) => (
                    <SelectItem key={c.id} value={c.id}>
                      {c.title}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </Field>
            <fieldset className="grid gap-3 sm:grid-cols-3">
              <legend className="mb-1 text-sm font-medium">A scheduled review of a branch (optional)</legend>
              <Field label="The branch">
                <Input value={branch} onChange={(e) => setBranch(e.target.value)} placeholder="develop" />
              </Field>
              <Field label="Over">
                <Input value={base} onChange={(e) => setBase(e.target.value)} placeholder="main" />
              </Field>
              <Field label="When (cron)">
                <Input value={cron} onChange={(e) => setCron(e.target.value)} placeholder="0 6 * * 1-5" />
              </Field>
            </fieldset>
            <div className="flex gap-2">
              <Button type="submit" loading={add.isPending} disabled={!project.trim()}>
                Review automatically
              </Button>
              <Button type="button" variant="ghost" onClick={() => setAdding(false)}>
                Cancel
              </Button>
            </div>
          </form>
        ) : (
          <div>
            <Button variant="outline" size="sm" onClick={() => setAdding(true)}>
              <Plus /> A project
            </Button>
          </div>
        )}
      </CardContent>
    </Card>
  )
}
