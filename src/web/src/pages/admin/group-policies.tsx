import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { Segmented } from '@/components/app/segmented'
import { Alert } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardFooter, CardHeader, CardTitle } from '@/components/ui/card'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { money } from '@/lib/format'
import { maxRequests, maxTokens, parseLimit } from '@/lib/rate-limits'
import { noPolicies, type GroupDetail, type GroupPolicies } from './groups-api'
import { creditKinds, parseCredit, type CreditKind, type Credits } from './people-api'

/** Radix's Select takes no empty value: this one stands for "the company's setting". */
const company = 'company'

const choices = {
  secretScanning: [
    ['refuse', 'Refuse the message or file'],
    ['mask', 'Mask each secret'],
    ['off', 'Let them through'],
  ],
  redactPii: [
    ['mask', 'Mask it'],
    ['off', 'As written'],
  ],
  moderation: [
    ['check', 'Check each message'],
    ['off', 'No check'],
  ],
  blockedPatterns: [
    ['on', 'Apply them'],
    ['off', 'Not for this group'],
  ],
} as const

type Choice = keyof typeof choices

/** A group's retention, credit, cost centre, safeguards and API keys' rate limits: one form, saved together. */
export function GroupPoliciesCard({ group }: { group: GroupDetail }) {
  const queryClient = useQueryClient()
  const p = group.policies ?? noPolicies
  const [days, setDays] = useState(p.retentionDays === null ? '' : String(p.retentionDays))
  const [credits, setCredits] = useState<Record<CreditKind, string>>(
    () => Object.fromEntries(creditKinds.map(({ kind }) => [kind, p.credits[kind] === null ? '' : String(p.credits[kind])])) as Record<CreditKind, string>,
  )
  const [perMember, setPerMember] = useState(p.creditPerMember ? 'member' : 'shared')
  const [costCentre, setCostCentre] = useState(p.costCentre ?? '')
  const [requests, setRequests] = useState(p.requestsPerMinute === null || p.requestsPerMinute === undefined ? '' : String(p.requestsPerMinute))
  const [tokens, setTokens] = useState(p.tokensPerMinute === null || p.tokensPerMinute === undefined ? '' : String(p.tokensPerMinute))
  const [checks, setChecks] = useState<Record<Choice, string>>({
    secretScanning: p.secretScanning ?? company,
    redactPii: p.redactPii ?? company,
    moderation: p.moderation ?? company,
    blockedPatterns: p.blockedPatterns === null ? company : p.blockedPatterns ? 'on' : 'off',
  })
  const [error, setError] = useState<string | null>(null)
  const save = useMutation({
    mutationFn: (body: GroupPolicies) => api(`/api/admin/groups/${group.id}/policies`, { method: 'PUT', body }),
    onSuccess: async () => {
      toast.success('Policies saved', { description: 'They hold at once.' })
      await queryClient.invalidateQueries({ queryKey: ['admin', 'groups'] })
    },
    onError: (e) => setError(errorMessage(e)),
  })
  const pick = (c: Choice) => (checks[c] === company ? null : checks[c])
  const submit = () => {
    setError(null)
    const d = days.trim()
    const retention = d === '' ? null : Number(d)
    if (retention !== null && !(Number.isInteger(retention) && retention >= 1 && retention <= 36500)) return setError('Keep chats a whole number of days, 1 to 36,500, or empty.')
    const c = Object.fromEntries(creditKinds.map(({ kind }) => [kind, parseCredit(credits[kind])]))
    if (Object.values(c).includes('invalid')) return setError('Each credit is a number of dollars, or empty for no group limit of that kind.')
    const limited = Object.values(c).some((v) => v !== null)
    const r = parseLimit(requests, maxRequests)
    const t = parseLimit(tokens, maxTokens)
    if (r === 'invalid' || t === 'invalid')
      return setError(`Limits are whole numbers: requests 0 to ${maxRequests.toLocaleString('en-US')}, tokens 0 to ${maxTokens.toLocaleString('en-US')} a minute. 0: no limit; empty: the company's setting.`)
    save.mutate({
      retentionDays: retention,
      credits: c as Credits,
      creditPerMember: limited && perMember === 'member',
      costCentre: costCentre.trim() || null,
      secretScanning: pick('secretScanning') as GroupPolicies['secretScanning'],
      redactPii: pick('redactPii') as GroupPolicies['redactPii'],
      moderation: pick('moderation') as GroupPolicies['moderation'],
      blockedPatterns: checks.blockedPatterns === company ? null : checks.blockedPatterns === 'on',
      requestsPerMinute: r,
      tokensPerMinute: t,
    })
  }
  const spent = group.spentThisMonth
  return (
    <Card>
      <CardHeader>
        <CardTitle>Policies</CardTitle>
        <CardDescription>For the members: how long their chats are kept, what they may spend, which safeguards apply, and how much their API keys may use a minute. Empty or the company&apos;s setting: Admin → Settings decides.</CardDescription>
      </CardHeader>
      <CardContent className="grid gap-5">
        {error && <Alert variant="destructive">{error}</Alert>}
        <div className="grid gap-4 sm:grid-cols-2">
          <Field label="Keep chats for (days)" hint="Then they are deleted with their files. A person in several groups keeps the shortest. People on legal hold keep everything.">
            <Input inputMode="numeric" value={days} onChange={(e) => setDays(e.target.value)} placeholder="the company's setting" />
          </Field>
          <Field label="Cost centre" hint="Its spend is charged to it in the monthly chargeback report.">
            <Input value={costCentre} onChange={(e) => setCostCentre(e.target.value)} maxLength={100} placeholder="none" />
          </Field>
          <fieldset className="grid gap-2 sm:col-span-2">
            <legend className="mb-1 text-sm font-medium">Credits a month ($)</legend>
            <p className="text-xs text-muted-foreground">One per kind, each held to what the members spent on it from the first of the month (UTC). Empty: no group limit of that kind.</p>
            <div className="grid gap-3 sm:grid-cols-5">
              {creditKinds.map(({ kind, label, hint }) => (
                <Field key={kind} label={label} hint={group.spentByKind ? `${money(group.spentByKind[kind])} spent` : undefined}>
                  <Input
                    inputMode="decimal"
                    aria-label={`${label} credit a month ($)`}
                    title={hint}
                    value={credits[kind]}
                    onChange={(e) => setCredits((v) => ({ ...v, [kind]: e.target.value }))}
                    placeholder="no limit"
                  />
                </Field>
              ))}
            </div>
          </fieldset>
          <div className="grid content-start gap-2">
            <span className="text-sm font-medium">The credits are</span>
            <Segmented
              label="The credits are"
              value={perMember}
              onChange={setPerMember}
              options={[
                { value: 'shared', label: 'Shared by the members' },
                { value: 'member', label: 'Each member’s' },
              ]}
            />
            {spent !== undefined && spent !== null && (
              <p className="text-xs text-muted-foreground">
                Spent this month, every kind together: {money(spent)}
              </p>
            )}
          </div>
        </div>
        <div className="grid gap-4 sm:grid-cols-2">
          <ChoiceField label="Secrets in messages and files" choice="secretScanning" value={checks.secretScanning} onChange={(v) => setChecks({ ...checks, secretScanning: v })} />
          <ChoiceField label="Personal data" choice="redactPii" value={checks.redactPii} onChange={(v) => setChecks({ ...checks, redactPii: v })} />
          <ChoiceField label="The model checks each message" choice="moderation" value={checks.moderation} onChange={(v) => setChecks({ ...checks, moderation: v })} />
          <ChoiceField label="Blocked words" choice="blockedPatterns" value={checks.blockedPatterns} onChange={(v) => setChecks({ ...checks, blockedPatterns: v })} />
        </div>
        <p className="text-xs text-muted-foreground">A person in several groups gets the strictest of the groups that set a safeguard. They apply in the chat and to API keys alike.</p>
        <div className="grid gap-4 sm:grid-cols-2">
          <Field label="Requests a minute, per key" hint="Each member's API key, at the gateway; past it, HTTP 429. 0: no limit. Empty: the company's setting.">
            <Input inputMode="numeric" value={requests} onChange={(e) => setRequests(e.target.value)} placeholder="the company's setting" />
          </Field>
          <Field label="Tokens a minute, per key" hint="What the model reads and writes for each member's key. 0: no limit. Empty: the company's setting.">
            <Input inputMode="numeric" value={tokens} onChange={(e) => setTokens(e.target.value)} placeholder="the company's setting" />
          </Field>
        </div>
        <p className="text-xs text-muted-foreground">A person in several groups gets the highest limit of the groups that set one (0, no limit, is the highest); their own (Admin → People) replaces it. The chat is not limited by these.</p>
      </CardContent>
      <CardFooter>
        <Button onClick={submit} loading={save.isPending}>
          Save policies
        </Button>
      </CardFooter>
    </Card>
  )
}

function ChoiceField({ label, choice, value, onChange }: { label: string; choice: Choice; value: string; onChange: (v: string) => void }) {
  return (
    <Field label={label}>
      <Select value={value} onValueChange={onChange}>
        <SelectTrigger>
          <SelectValue />
        </SelectTrigger>
        <SelectContent>
          <SelectItem value={company}>The company&apos;s setting</SelectItem>
          {choices[choice].map(([v, text]) => (
            <SelectItem key={v} value={v}>
              {text}
            </SelectItem>
          ))}
        </SelectContent>
      </Select>
    </Field>
  )
}
