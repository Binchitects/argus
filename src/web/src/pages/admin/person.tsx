import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ArrowLeft, KeyRound, LogOut, RefreshCw, ShieldOff, Trash2, UserCheck, UserCog, UserX } from 'lucide-react'
import { useState } from 'react'
import { Link, useNavigate, useOutletContext, useParams } from 'react-router'
import { KeyValues } from '@/components/app/key-values'
import { PageSkeleton, QueryError } from '@/components/app/query-state'
import { LimitRows } from '@/components/app/rate-limits'
import { Secret } from '@/components/app/secret'
import { Alert } from '@/components/ui/alert'
import { Avatar } from '@/components/ui/avatar'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardFooter, CardHeader, CardTitle } from '@/components/ui/card'
import { useConfirm } from '@/components/ui/confirm'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Switch } from '@/components/ui/switch'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage, type Me } from '@/lib/api'
import { ago, money, when } from '@/lib/format'
import { maxRequests, maxTokens, parseLimit, type KeyLimits } from '@/lib/rate-limits'
import { LegalHoldCard } from './legal-hold'
import { CreditMeter, PersonBadges } from './person-badges'
import { creditKinds, parseCredit, personQuery, type CreditKind, type Person, type PersonDetail, type Standing } from './people-api'

const sourceLabel: Record<Person['source'], string> = { local: 'Local account', ldap: 'Company directory', oidc: 'Company sign-in' }
const signsInWith: Record<Person['source'], string> = {
  local: 'a password here',
  ldap: 'the company directory (LDAP)',
  oidc: "the company's identity provider (OIDC)",
}

export function PersonPage() {
  const { id = '' } = useParams()
  const me = useOutletContext<Me>()
  const detail = useQuery(personQuery(id))
  const [secret, setSecret] = useState<{ label: string; value: string } | null>(null)

  if (detail.isPending) return <PageSkeleton />
  if (detail.error) return <QueryError error={detail.error} retry={() => detail.refetch()} />
  const { person: p, keys, warning, groups, directoryGroups, limits, standing } = detail.data
  const self = p.id === me.id

  return (
    <>
      <Button variant="ghost" size="sm" asChild className="mb-4 -ml-2 text-muted-foreground">
        <Link to="/admin/people">
          <ArrowLeft /> People
        </Link>
      </Button>
      <div className="mb-6 flex flex-wrap items-center gap-4">
        <Avatar name={p.displayName || p.userName} className="size-14 text-lg" />
        <div className="min-w-0">
          <h1 className="text-2xl font-semibold tracking-tight">{p.displayName}</h1>
          <p className="text-muted-foreground">
            {p.userName} · {p.email}
          </p>
          <div className="mt-2 flex flex-wrap gap-1.5">
            <Badge variant={p.isAdmin ? 'default' : 'secondary'}>{p.isAdmin ? 'Admin' : 'Member'}</Badge>
            <Badge variant="outline">{sourceLabel[p.source]}</Badge>
            <PersonBadges p={p} />
            {p.legalHoldSince && <Badge variant="warning">Legal hold</Badge>}
            {self && <Badge variant="outline">You</Badge>}
          </div>
        </div>
      </div>
      {warning && (
        <Alert variant="warning" className="mb-4">
          {warning}
        </Alert>
      )}
      {secret && (
        <Alert variant="success" title={`${secret.label} — shown only this once`} className="mb-4">
          <div className="mt-2">
            <Secret label={secret.label} value={secret.value} />
          </div>
        </Alert>
      )}
      <div className="grid gap-6 lg:grid-cols-2">
        <div className="grid content-start gap-6">
          <Profile p={p} />
          <Groups groups={groups} directoryGroups={directoryGroups} />
          <CreditsCard key={creditKinds.map((k) => p.credits[k.kind].credit).join('-')} p={p} standing={standing} />
          <ApiKeyCard p={p} keys={keys} onSecret={setSecret} />
          {limits && <RateLimitsCard key={`${limits.own.requestsPerMinute}-${limits.own.tokensPerMinute}`} p={p} limits={limits} />}
        </div>
        <div className="grid content-start gap-6">
          <Access p={p} self={self} onSecret={setSecret} />
          <LegalHoldCard p={p} />
          {!self && <Danger p={p} />}
        </div>
      </div>
    </>
  )
}

function useRefresh(id: string) {
  const queryClient = useQueryClient()
  return () => {
    void queryClient.invalidateQueries({ queryKey: ['admin', 'person', id] })
    void queryClient.invalidateQueries({ queryKey: ['admin', 'people'] })
  }
}

function Profile({ p }: { p: Person }) {
  const refresh = useRefresh(p.id)
  const [name, setName] = useState<string | null>(null)
  const rename = useMutation({
    mutationFn: (displayName: string) => api(`/api/admin/people/${p.id}`, { method: 'PATCH', body: { displayName } }),
    onSuccess: () => {
      setName(null)
      toast.success('Name changed')
      refresh()
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  return (
    <Card>
      <CardHeader>
        <CardTitle>Profile</CardTitle>
      </CardHeader>
      <CardContent className="grid gap-4">
        <KeyValues
          items={[
            ['Signs in with', signsInWith[p.source]],
            ['Last sign-in', p.lastSignInAt ? `${ago(p.lastSignInAt)} (${when(p.lastSignInAt)})` : 'never'],
            ['Added', when(p.createdAt)],
          ]}
        />
        {p.source === 'local' &&
          (name === null ? (
            <div>
              <Button variant="outline" size="sm" onClick={() => setName(p.displayName)}>
                Change name
              </Button>
            </div>
          ) : (
            <form
              className="flex flex-wrap items-end gap-2"
              onSubmit={(e) => {
                e.preventDefault()
                if (name.trim()) rename.mutate(name.trim())
              }}
            >
              <Field label="Name" className="min-w-48 flex-1">
                <Input value={name} onChange={(e) => setName(e.target.value)} autoFocus />
              </Field>
              <Button type="submit" loading={rename.isPending}>
                Save
              </Button>
              <Button type="button" variant="ghost" onClick={() => setName(null)}>
                Cancel
              </Button>
            </form>
          ))}
      </CardContent>
    </Card>
  )
}

function Access({ p, self, onSecret }: { p: Person; self: boolean; onSecret: (s: { label: string; value: string }) => void }) {
  const refresh = useRefresh(p.id)
  const confirm = useConfirm()
  const act = useMutation({
    mutationFn: ({ path, method = 'POST', body = {} }: { path: string; method?: string; body?: object; done: string }) =>
      api<{ password?: string } | undefined>(`/api/admin/people/${p.id}${path}`, { method, body }),
    onSuccess: (r, v) => {
      if (r?.password) onSecret({ label: 'New password', value: r.password })
      toast.success(v.done)
      refresh()
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  // The directory or the company's identity provider decides their password and role.
  const managed = p.source !== 'local'
  const ask = async (title: string, description: string, confirmText: string, run: () => void, destructive = true) => {
    if (await confirm({ title, description, confirm: confirmText, destructive })) run()
  }
  return (
    <Card>
      <CardHeader>
        <CardTitle>Access</CardTitle>
        <CardDescription>
          {p.source === 'ldap' ? "The directory decides this person's password and role." : p.source === 'oidc' ? "The company's identity provider decides this person's password, two-factor sign-in and role." : 'Role, sign-in and sessions.'}
        </CardDescription>
      </CardHeader>
      <CardContent className="flex flex-wrap gap-2">
        {!managed && !self && (
          <Button
            variant="outline"
            onClick={() =>
              ask(
                p.isAdmin ? `Remove admin from ${p.displayName}?` : `Make ${p.displayName} an admin?`,
                p.isAdmin ? 'They keep their account and credit, and lose the admin pages.' : 'Admins manage people, settings and the model.',
                p.isAdmin ? 'Remove admin' : 'Make admin',
                () => act.mutate({ path: '', method: 'PATCH', body: { admin: !p.isAdmin }, done: p.isAdmin ? 'No longer an admin' : 'Now an admin' }),
                p.isAdmin,
              )
            }
          >
            <UserCog /> {p.isAdmin ? 'Remove admin' : 'Make admin'}
          </Button>
        )}
        {!self && (
          <Button
            variant="outline"
            onClick={() =>
              p.disabled
                ? act.mutate({ path: '', method: 'PATCH', body: { disabled: false }, done: 'Enabled' })
                : ask(`Disable ${p.displayName}?`, 'They are signed out and their API keys stop working until enabled again.', 'Disable', () =>
                    act.mutate({ path: '', method: 'PATCH', body: { disabled: true }, done: 'Disabled' }),
                  )
            }
          >
            {p.disabled ? <UserCheck /> : <UserX />} {p.disabled ? 'Enable' : 'Disable'}
          </Button>
        )}
        {!managed && (
          <Button
            variant="outline"
            onClick={() => ask(`Give ${p.displayName} a new password?`, 'The old one stops working at once. You will see the new one here, once.', 'Reset password', () => act.mutate({ path: '/password', done: 'Password reset' }))}
          >
            <KeyRound /> Reset password
          </Button>
        )}
        {p.twoFactorEnabled && (
          <Button
            variant="outline"
            onClick={() => ask(`Turn off two-factor sign-in for ${p.displayName}?`, 'For a lost phone. They can set it up again from their account page.', 'Turn off 2FA', () => act.mutate({ path: '/2fa/reset', done: 'Two-factor sign-in turned off' }))}
          >
            <ShieldOff /> Reset 2FA
          </Button>
        )}
        <Button variant="outline" onClick={() => act.mutate({ path: '/sign-out', done: 'Signed out everywhere' })}>
          <LogOut /> Sign out everywhere
        </Button>
      </CardContent>
    </Card>
  )
}

/** Their own credit of each kind for a calendar month, with this month's spend; the tightest group's credit beside it. */
function CreditsCard({ p, standing }: { p: Person; standing: Standing[] | null }) {
  const refresh = useRefresh(p.id)
  const [values, setValues] = useState<Record<CreditKind, string>>(
    () => Object.fromEntries(creditKinds.map(({ kind }) => [kind, p.credits[kind].credit === null ? '' : String(p.credits[kind].credit)])) as Record<CreditKind, string>,
  )
  const [error, setError] = useState<string | null>(null)
  const save = useMutation({
    mutationFn: (credits: Record<CreditKind, number | null>) => api(`/api/admin/people/${p.id}/credits`, { method: 'PUT', body: credits }),
    onSuccess: () => {
      toast.success('Credits set', { description: 'They hold at once, for the chat and for API keys.' })
      refresh()
    },
    onError: (e) => setError(errorMessage(e)),
  })
  return (
    <Card>
      <CardHeader>
        <CardTitle>Credits this month</CardTitle>
        <CardDescription>One per kind, each held to what they spent this month (UTC) on it, whichever way. Empty: no limit of their own.</CardDescription>
      </CardHeader>
      <CardContent>
        <form
          className="grid gap-3"
          onSubmit={(e) => {
            e.preventDefault()
            setError(null)
            const parsed = Object.fromEntries(creditKinds.map(({ kind }) => [kind, parseCredit(values[kind])]))
            if (Object.values(parsed).includes('invalid')) setError('Each credit is a number of dollars, or empty for no limit.')
            else save.mutate(parsed as Record<CreditKind, number | null>)
          }}
        >
          {error && <Alert variant="destructive">{error}</Alert>}
          {creditKinds.map(({ kind, label, hint }) => {
            const group = standing?.find((s) => s.kind === kind)
            return (
              <div key={kind} className="grid grid-cols-[minmax(0,1fr)_8rem] items-center gap-3">
                <div className="grid min-w-0 gap-1">
                  <span className="flex flex-wrap items-baseline gap-x-2 text-sm font-medium">
                    {label} <span className="text-xs font-normal text-muted-foreground">{hint}</span>
                  </span>
                  <CreditMeter spend={p.credits[kind].spent} budget={p.credits[kind].credit} />
                  {group?.group && (
                    <span className="text-xs text-muted-foreground">
                      {group.group}: {money(group.groupLeft ?? 0)} left this month
                    </span>
                  )}
                </div>
                <Input
                  inputMode="decimal"
                  aria-label={`${label} credit ($)`}
                  value={values[kind]}
                  onChange={(e) => setValues((v) => ({ ...v, [kind]: e.target.value }))}
                  placeholder="no limit"
                />
              </div>
            )
          })}
          <div>
            <Button type="submit" variant="outline" loading={save.isPending}>
              Set credits
            </Button>
          </div>
        </form>
      </CardContent>
    </Card>
  )
}

/** API access (on for everyone; an admin can take it), and their keys. */
function ApiKeyCard({ p, keys, onSecret }: { p: Person; keys: PersonDetail['keys']; onSecret: (s: { label: string; value: string }) => void }) {
  const refresh = useRefresh(p.id)
  const confirm = useConfirm()
  const access = useMutation({
    mutationFn: (on: boolean) => api<{ warning: string | null }>(`/api/admin/people/${p.id}/api`, { method: 'PUT', body: { on } }),
    onSuccess: (r, on) => {
      if (r.warning) toast.warning(on ? 'API access on' : 'API access off', { description: r.warning })
      else
        toast.success(on ? 'API access on' : 'API access off', {
          description: on ? (p.disabled ? 'Their keys work again once they are enabled.' : 'Their keys work again.') : 'Their keys are blocked at once.',
        })
      refresh()
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const rotate = useMutation({
    mutationFn: () => api<{ apiKey: string }>(`/api/admin/people/${p.id}/key`, { body: {} }),
    onSuccess: (r) => {
      onSecret({ label: 'New API key', value: r.apiKey })
      refresh()
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  return (
    <Card>
      <CardHeader>
        <CardTitle>API key</CardTitle>
        <CardDescription>Coding agents, IDEs and scripts reach the models with it. Their text requests count to the API credit.</CardDescription>
      </CardHeader>
      <CardContent className="grid gap-4">
        <Label className="flex items-start gap-3 font-normal">
          <Switch
            checked={!p.apiOff}
            disabled={access.isPending}
            onCheckedChange={async (on) => {
              if (
                on ||
                (await confirm({
                  title: `Take ${p.displayName}'s API access?`,
                  description: 'Their keys are blocked at once, none is shown to them or made, and Arena MCP and Argus refuse them. The chat stays theirs.',
                  confirm: 'Take API access',
                  destructive: true,
                }))
              )
                access.mutate(on)
            }}
            aria-describedby={`api-${p.id}`}
          />
          <span className="grid gap-0.5">
            <span className="font-medium">API access</span>
            <span id={`api-${p.id}`} className="text-xs text-muted-foreground">
              {p.apiOff
                ? 'Off: their keys are blocked, and they cannot make one.'
                : p.disabled
                  ? 'On, but they are disabled: their keys work once they are enabled.'
                  : 'On: their keys work, within their API credit.'}
            </span>
          </span>
        </Label>
        <ul className="grid gap-2">
          {keys.length === 0 && <li className="text-sm text-muted-foreground">No API key.</li>}
          {keys.map((k) => (
            <li key={k.alias + k.preview} className="flex flex-wrap items-center gap-3 rounded-lg border px-3 py-2 text-sm">
              <KeyRound className="size-4 text-muted-foreground" aria-hidden="true" />
              <code className="font-mono text-[0.8125rem]">{k.preview ?? k.alias}</code>
              {k.blocked && <Badge variant="destructive">Blocked</Badge>}
              <span className="ml-auto text-xs text-muted-foreground tabular-nums">{money(k.spend)} spent</span>
            </li>
          ))}
        </ul>
      </CardContent>
      {!p.apiOff && (
        <CardFooter>
          <Button
            variant="outline"
            loading={rotate.isPending}
            onClick={async () => {
              if (await confirm({ title: `New API key for ${p.displayName}?`, description: 'The current key stops working at once.', confirm: 'Make a new key', destructive: true })) rotate.mutate()
            }}
          >
            <RefreshCw /> New API key
          </Button>
        </CardFooter>
      )}
    </Card>
  )
}

const limitText = (v: number | null) => (v === null ? '' : String(v))

/** Their API keys' requests and tokens a minute: what applies now, and their own (empty: their groups' or the company's; 0: no limit). */
function RateLimitsCard({ p, limits }: { p: Person; limits: KeyLimits }) {
  const refresh = useRefresh(p.id)
  const [requests, setRequests] = useState(limitText(limits.own.requestsPerMinute))
  const [tokens, setTokens] = useState(limitText(limits.own.tokensPerMinute))
  const [error, setError] = useState<string | null>(null)
  const save = useMutation({
    mutationFn: (body: { requestsPerMinute: number | null; tokensPerMinute: number | null }) =>
      api<{ warning: string | null }>(`/api/admin/people/${p.id}/limits`, { method: 'PUT', body }),
    onSuccess: (r) => {
      if (r?.warning) toast.warning('Limits saved', { description: r.warning })
      else toast.success('Limits set', { description: 'Their API keys have them at once.' })
      refresh()
    },
    onError: (e) => setError(errorMessage(e)),
  })
  return (
    <Card>
      <CardHeader>
        <CardTitle>Rate limits</CardTitle>
        <CardDescription>What each of their API keys may use a minute at the gateway; past it, requests are refused (HTTP 429). The chat is not limited by these.</CardDescription>
      </CardHeader>
      <CardContent className="grid gap-4">
        <LimitRows limits={limits} you={false} />
        <form
          className="flex flex-wrap items-end gap-2"
          onSubmit={(e) => {
            e.preventDefault()
            setError(null)
            const r = parseLimit(requests, maxRequests)
            const t = parseLimit(tokens, maxTokens)
            if (r === 'invalid' || t === 'invalid') setError(`Whole numbers: requests 0 to ${maxRequests.toLocaleString('en-US')}, tokens 0 to ${maxTokens.toLocaleString('en-US')}. 0: no limit; empty: their groups' or the company's.`)
            else save.mutate({ requestsPerMinute: r, tokensPerMinute: t })
          }}
        >
          <Field label="Requests a minute" className="w-40">
            <Input inputMode="numeric" value={requests} onChange={(e) => setRequests(e.target.value)} placeholder="from groups" />
          </Field>
          <Field label="Tokens a minute" className="w-40">
            <Input inputMode="numeric" value={tokens} onChange={(e) => setTokens(e.target.value)} placeholder="from groups" />
          </Field>
          <Button type="submit" variant="outline" loading={save.isPending}>
            Set limits
          </Button>
        </form>
        {error ? (
          <Alert variant="destructive">{error}</Alert>
        ) : (
          <p className="text-xs text-muted-foreground">Their own replace their groups&apos; and the company&apos;s. Empty: from their groups (the highest), else the company&apos;s. 0: no limit.</p>
        )}
      </CardContent>
    </Card>
  )
}

function Danger({ p }: { p: Person }) {
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const [open, setOpen] = useState(false)
  const [typed, setTyped] = useState('')
  const remove = useMutation({
    mutationFn: () => api(`/api/admin/people/${p.id}`, { method: 'DELETE' }),
    onSuccess: () => {
      toast.success(`${p.displayName} deleted`)
      void queryClient.invalidateQueries({ queryKey: ['admin', 'people'] })
      navigate('/admin/people')
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  return (
    <Card className="border-destructive/30">
      <CardHeader>
        <CardTitle>Delete</CardTitle>
        <CardDescription>Removes the person, their chats and their API keys. Their usage history stays in the reports.</CardDescription>
      </CardHeader>
      <CardContent>
        <Button variant="destructive" onClick={() => setOpen(true)}>
          <Trash2 /> Delete {p.userName}
        </Button>
      </CardContent>
      <Dialog open={open} onOpenChange={(o) => { setOpen(o); setTyped('') }}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Delete {p.displayName}?</DialogTitle>
            <DialogDescription>This cannot be undone. Type {p.userName} to confirm.</DialogDescription>
          </DialogHeader>
          <Field label="Username">
            <Input value={typed} onChange={(e) => setTyped(e.target.value)} autoComplete="off" autoFocus />
          </Field>
          <DialogFooter>
            <Button variant="outline" onClick={() => setOpen(false)}>
              Cancel
            </Button>
            <Button variant="destructive" disabled={typed !== p.userName} loading={remove.isPending} onClick={() => remove.mutate()}>
              Delete
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </Card>
  )
}

function Groups({ groups, directoryGroups }: { groups: PersonDetail['groups']; directoryGroups: string[] }) {
  return (
    <Card>
      <CardHeader>
        <CardTitle>Groups</CardTitle>
        <CardDescription>What they belong to decides which tools and models they may use.</CardDescription>
      </CardHeader>
      <CardContent className="grid gap-3">
        {groups.length === 0 ? (
          <p className="text-sm text-muted-foreground">
            In no group. <Link to="/admin/groups" className="underline underline-offset-2">Groups</Link>
          </p>
        ) : (
          <ul className="flex flex-wrap gap-1.5">
            {groups.map((g) => (
              <li key={g.id}>
                <Link to={`/admin/groups/${g.id}`} className="rounded-md outline-none focus-visible:ring-[3px] focus-visible:ring-ring">
                  <Badge variant={g.directory ? 'outline' : 'secondary'}>{g.name}</Badge>
                </Link>
              </li>
            ))}
          </ul>
        )}
        {directoryGroups.length > 0 && (
          <details className="text-sm">
            <summary className="cursor-pointer text-muted-foreground">In the directory: {directoryGroups.length} groups</summary>
            <ul className="mt-2 grid gap-1 font-mono text-xs break-all text-muted-foreground">
              {directoryGroups.map((d) => (
                <li key={d}>{d}</li>
              ))}
            </ul>
          </details>
        )}
      </CardContent>
    </Card>
  )
}
