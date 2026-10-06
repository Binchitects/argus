import { useQuery } from '@tanstack/react-query'
import { Coins, Database, DatabaseZap, Hash, MessageSquareText } from 'lucide-react'
import { useState } from 'react'
import { Link } from 'react-router'
import { QueryError } from '@/components/app/query-state'
import { Segmented } from '@/components/app/segmented'
import { Stat, StatGrid } from '@/components/app/stat'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { DataTable, SortHeader, type ColumnDef } from '@/components/ui/data-table'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { api } from '@/lib/api'
import { formatValue, money, when } from '@/lib/format'
import { presets, resolve } from '@/lib/time'
import { groupsQuery } from './admin/groups-api'
import { peopleQuery } from './admin/people-api'

/** One prompt and what it cost: a chat answer (rounds, tool calls and sub-agents together) or a request with an API key. */
export interface PromptRow {
  id: string
  at: string
  source: 'chat' | 'api'
  /** What it made: chat, picture, speech, transcription or video. */
  kind: string
  model: string | null
  prompt: number
  cached: number
  completion: number
  cost: number
  /** Parts of it ran before costs were kept: its cost leaves them out. */
  unpriced: boolean
  chatId?: string | null
  title?: string | null
  /** The API key's name. */
  key?: string | null
  person?: { id: string | null; email: string; name: string | null } | null
}

export interface PromptListData {
  rows: PromptRow[]
  /** Every prompt the filters match, not only the rows sent. */
  totals: { prompts: number; prompt: number; cached: number; completion: number; cost: number; unpriced: number }
  capped: boolean
  models: string[]
  problem: string | null
}

const ALL = '__all__'

const sources = [
  { value: 'all', label: 'All' },
  { value: 'chat', label: 'Chat' },
  { value: 'api', label: 'API keys' },
] as const

type Source = (typeof sources)[number]['value']

/** Where a prompt came from, in words: the chat's title (a link), or the API key that made it. */
function Origin({ row, mine }: { row: PromptRow; mine: boolean }) {
  const kind = row.kind !== 'chat' && <Badge variant="outline">{row.kind}</Badge>
  if (row.source === 'chat')
    return mine && row.chatId ? (
      <Link to={`/chat/${row.chatId}`} className="line-clamp-1 font-medium underline-offset-4 hover:underline">
        {row.title || 'Untitled chat'}
      </Link>
    ) : (
      <span>Chat</span>
    )
  return (
    <span className="inline-flex flex-wrap items-center gap-1.5">
      <span className="text-muted-foreground">API key</span> <span className="font-medium">{row.key}</span> {kind}
    </span>
  )
}

const tokens = (title: string, key: 'prompt' | 'cached' | 'completion'): ColumnDef<PromptRow> => ({
  id: key,
  accessorFn: (r) => r[key],
  header: ({ column }) => <SortHeader column={column} title={title} />,
  cell: ({ getValue }) => <span className="tabular-nums">{formatValue(getValue<number>())}</span>,
})

function columns(mine: boolean): ColumnDef<PromptRow>[] {
  return [
    {
      id: 'at',
      accessorFn: (r) => new Date(r.at).getTime(),
      header: ({ column }) => <SortHeader column={column} title="When" />,
      cell: ({ row: { original: r } }) => (
        <time dateTime={r.at} className="whitespace-nowrap text-muted-foreground">
          {when(r.at)}
        </time>
      ),
    },
    ...(mine
      ? []
      : [
          {
            id: 'person',
            accessorFn: (r: PromptRow) => r.person?.name ?? r.person?.email ?? '(unattributed)',
            header: ({ column }) => <SortHeader column={column} title="Person" />,
            cell: ({ row: { original: r } }) => <span className="font-medium">{r.person?.name ?? r.person?.email ?? '(unattributed)'}</span>,
          } satisfies ColumnDef<PromptRow>,
        ]),
    {
      id: 'origin',
      accessorFn: (r) => (r.source === 'chat' ? (mine ? (r.title ?? 'Chat') : 'Chat') : `API key ${r.key} ${r.kind}`),
      header: ({ column }) => <SortHeader column={column} title={mine ? 'Chat or key' : 'From'} />,
      cell: ({ row: { original: r } }) => <Origin row={r} mine={mine} />,
    },
    { id: 'model', accessorFn: (r) => r.model ?? '', header: ({ column }) => <SortHeader column={column} title="Model" />, cell: ({ getValue }) => getValue<string>() || '—' },
    tokens('In', 'prompt'),
    tokens('Cached', 'cached'),
    tokens('Out', 'completion'),
    {
      id: 'cost',
      accessorFn: (r) => r.cost,
      header: ({ column }) => <SortHeader column={column} title="Cost" />,
      cell: ({ row: { original: r } }) =>
        r.unpriced ? (
          <span className="tabular-nums text-muted-foreground" title="Parts of it ran before costs were kept: an admin can work them out (Settings → Prices)">
            {money(r.cost)} *
          </span>
        ) : (
          <span className="tabular-nums">{money(r.cost)}</span>
        ),
    },
  ]
}

/** A time range for everyone's prompts: one ending now, or between two days. */
function useRange() {
  const [preset, setPreset] = useState('now-7d')
  const [from, setFrom] = useState('')
  const [to, setTo] = useState('')
  const custom = preset === 'dates'
  const range = (): { from: Date; to: Date } =>
    custom && from && to ? { from: new Date(`${from}T00:00:00`), to: new Date(new Date(`${to}T00:00:00`).getTime() + 86_400_000) } : { from: resolve(custom ? 'now-7d' : preset), to: new Date() }
  const control = (
    <div className="flex flex-wrap items-end gap-2">
      <Select value={preset} onValueChange={setPreset}>
        <SelectTrigger className="h-9 w-44" aria-label="Time range">
          <SelectValue />
        </SelectTrigger>
        <SelectContent>
          {presets.map((p) => (
            <SelectItem key={p.from} value={p.from}>
              {p.label}
            </SelectItem>
          ))}
          <SelectItem value="dates">Between dates</SelectItem>
        </SelectContent>
      </Select>
      {custom && (
        <>
          <Label className="grid gap-1 text-xs font-normal">
            From
            <Input type="date" className="h-9 w-40" value={from} onChange={(e) => setFrom(e.target.value)} />
          </Label>
          <Label className="grid gap-1 text-xs font-normal">
            To (inclusive)
            <Input type="date" className="h-9 w-40" value={to} onChange={(e) => setTo(e.target.value)} />
          </Label>
        </>
      )}
    </div>
  )
  return { key: custom ? `${from}..${to}` : preset, range, control }
}

/**
 * Prompts and what each cost: the signed-in person's (their chats by title, their API keys by
 * name), or everyone's for an admin (by person, group, model and dates; who, when, which model,
 * tokens and cost, never a chat's title). Totals count every prompt the filters match.
 */
export function PromptList({ everyone = false, from: fixedFrom }: { everyone?: boolean; from?: string }) {
  const own = useRange()
  const [source, setSource] = useState<Source>('all')
  const [model, setModel] = useState(ALL)
  const [person, setPerson] = useState(ALL)
  const [group, setGroup] = useState(ALL)
  const people = useQuery({ ...peopleQuery, enabled: everyone })
  const groups = useQuery({ ...groupsQuery, enabled: everyone })
  const rangeKey = fixedFrom ?? own.key
  const data = useQuery({
    queryKey: ['usage', 'prompts', everyone, rangeKey, source, model, person, group],
    queryFn: ({ signal }) => {
      const r = fixedFrom ? { from: resolve(fixedFrom), to: new Date() } : own.range()
      const q = new URLSearchParams({ from: r.from.toISOString(), to: r.to.toISOString(), source })
      if (model !== ALL) q.set('model', model)
      if (everyone && person !== ALL) q.set('person', person)
      if (everyone && group !== ALL) q.set('group', group)
      return api<PromptListData>(`${everyone ? '/api/admin/usage/prompts' : '/api/usage/prompts'}?${q}`, { signal })
    },
  })
  const d = data.data
  const t = d?.totals
  const models = d ? [...new Set([...d.models, ...(model === ALL ? [] : [model])])] : []
  return (
    <div className="grid gap-4">
      <div className="flex flex-wrap items-end gap-2">
        {!fixedFrom && own.control}
        <Segmented label="Where from" value={source} onChange={setSource} options={[...sources]} />
        <Select value={model} onValueChange={setModel}>
          <SelectTrigger className="h-9 w-52" aria-label="Model">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value={ALL}>Every model</SelectItem>
            {models.map((m) => (
              <SelectItem key={m} value={m}>
                {m}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
        {everyone && (
          <>
            <Select value={person} onValueChange={setPerson}>
              <SelectTrigger className="h-9 w-52" aria-label="Person">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value={ALL}>Everyone</SelectItem>
                {(people.data?.people ?? []).map((p) => (
                  <SelectItem key={p.id} value={p.id}>
                    {p.displayName || p.userName} ({p.email})
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
            <Select value={group} onValueChange={setGroup}>
              <SelectTrigger className="h-9 w-48" aria-label="Group">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value={ALL}>Every group</SelectItem>
                {(groups.data ?? []).map((g) => (
                  <SelectItem key={g.id} value={g.id}>
                    {g.name}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </>
        )}
      </div>
      {data.error && <QueryError error={data.error} retry={() => data.refetch()} />}
      {d?.problem && <Alert variant="warning">{d.problem}</Alert>}
      {t && (
        <StatGrid>
          <Stat icon={Hash} label="Prompts" value={formatValue(t.prompts)} />
          <Stat icon={Database} label="In" value={formatValue(t.prompt)} />
          <Stat icon={DatabaseZap} label="Cached" value={formatValue(t.cached)} hint={t.prompt ? `${formatValue((100 * t.cached) / t.prompt, 'percent')} of in` : undefined} />
          <Stat icon={MessageSquareText} label="Out" value={formatValue(t.completion)} />
          <Stat icon={Coins} label="Cost" value={money(t.cost)} hint={t.unpriced ? `${formatValue(t.unpriced)} with parts from before costs were kept` : undefined} />
        </StatGrid>
      )}
      {d?.capped && (
        <p className="text-sm text-muted-foreground">
          The table shows the newest {formatValue(d.rows.length)} of {formatValue(t!.prompts)} prompts; the totals count them all. Narrow the dates or filters to see the rest.
        </p>
      )}
      <DataTable
        columns={columns(!everyone)}
        data={d?.rows}
        loading={data.isPending}
        noun="prompts"
        getRowId={(r) => `${r.source}-${r.id}`}
        initialSorting={[{ id: 'at', desc: true }]}
        empty="No prompts in this time range."
      />
      {d?.rows.some((r) => r.unpriced) && (
        <p className="text-xs text-muted-foreground">* Parts of it ran before costs were kept, and its cost leaves them out. An admin can work them out at today's prices (Settings → Prices → Recalculate past costs).</p>
      )}
    </div>
  )
}
