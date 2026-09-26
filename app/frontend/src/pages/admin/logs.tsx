import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { Radio, RefreshCw } from 'lucide-react'
import { useEffect, useId, useMemo, useRef, useState } from 'react'
import { useSearchParams } from 'react-router'
import { CodeBlock } from '@/components/app/code-block'
import { PageHeader } from '@/components/app/page-header'
import { Pick } from '@/components/app/pick'
import { QueryError } from '@/components/app/query-state'
import { RangeSelect } from '@/components/app/range-select'
import { Segmented } from '@/components/app/segmented'
import type { ChartSeries } from '@/components/charts/fold'
import { TimeChart } from '@/components/charts/time-chart'
import { LogsPanel } from '@/components/dashboards/logs-panel'
import type { LogLine } from '@/components/dashboards/types'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Skeleton } from '@/components/ui/skeleton'
import { api } from '@/lib/api'
import { intervalFor, resolve } from '@/lib/time'
import { cn } from '@/lib/utils'

interface Lines {
  query: string
  lines: LogLine[]
  limit: number
  more: boolean
}

/** Lines per interval by level, and the range they span (the chart's axis). */
interface Volume {
  intervalMs: number
  series: ChartSeries[]
  from: number
  to: number
}

const LIMIT = 1000
/** Lines kept while live: the newest, so a page left open stays light. */
const KEEP = 5000
const ALL = '*'

const levels = [
  { value: 'all', label: 'All' },
  { value: 'warn', label: 'Warnings and errors' },
  { value: 'error', label: 'Errors' },
]

/** A level's colour is its state (not an identity): the same as a line's edge. */
const levelColors: Record<string, string> = { Errors: '--destructive', Warnings: '--warning', Info: '--primary', Other: '--muted-foreground' }
const levelNames: Record<string, string> = { error: 'Errors', warn: 'Warnings', info: 'Info', other: 'Other' }

/**
 * Every service's logs from Loki, newest first: by container, level and text,
 * and live (new lines every few seconds). The filters are in the address, so
 * a view can be linked.
 */
export function LogsPage() {
  const [params, setParams] = useSearchParams()
  const from = params.get('range') ?? 'now-1h'
  const containers = params.getAll('container')
  const level = params.get('level') ?? 'all'
  const search = params.get('q') ?? ''
  const [live, setLive] = useState(false)
  const set = (change: (p: URLSearchParams) => void) =>
    setParams((prev) => {
      const p = new URLSearchParams(prev)
      change(p)
      return p
    }, { replace: true })

  // The filters as the API takes them; the range is resolved when a request goes out.
  const containerKey = containers.join('\n')
  const filter = useMemo(() => {
    const p = new URLSearchParams()
    for (const c of containerKey.split('\n')) if (c) p.append('container', c)
    if (level !== 'all') p.set('level', level)
    if (search) p.set('search', search)
    return p.toString()
  }, [containerKey, level, search])
  const key = `${from}?${filter}`

  const names = useQuery({
    queryKey: ['admin', 'logs', 'containers'],
    queryFn: ({ signal }) => api<{ containers: string[] }>('/api/admin/logs/containers', { signal }),
    staleTime: 60_000,
  })
  const lines = useQuery({
    queryKey: ['admin', 'logs', 'lines', key],
    queryFn: ({ signal }) => {
      const to = new Date()
      return api<Lines>(`/api/admin/logs/?${filter}&from=${resolve(from, to).toISOString()}&to=${to.toISOString()}&limit=${LIMIT}`, { signal })
    },
    placeholderData: keepPreviousData,
  })
  const volume = useQuery({
    queryKey: ['admin', 'logs', 'volume', key],
    queryFn: ({ signal }): Promise<Volume> => {
      const to = new Date()
      const start = resolve(from, to)
      return api<Omit<Volume, 'from' | 'to'>>(
        `/api/admin/logs/volume?${filter}&from=${start.toISOString()}&to=${to.toISOString()}&intervalMs=${intervalFor(start, to, 120)}`,
        { signal },
      ).then((v) => ({ ...v, from: start.getTime(), to: to.getTime() }))
    },
    placeholderData: keepPreviousData,
    refetchInterval: live ? 15_000 : false,
  })

  // Live: the lines written since the newest one here, every three seconds.
  const [tail, setTail] = useState<{ key: string; lines: LogLine[] }>({ key: '', lines: [] })
  const shown = useMemo(() => [...(tail.key === key ? tail.lines : []), ...(lines.data?.lines ?? [])].slice(0, KEEP), [tail, key, lines.data])
  const newest = useRef<string | undefined>(undefined)
  useEffect(() => {
    newest.current = shown[0]?.nanos
  }, [shown])
  const ready = lines.isSuccess && !lines.isPlaceholderData
  useEffect(() => {
    if (!live || !ready) return
    const controller = new AbortController()
    let busy = false
    const t = setInterval(async () => {
      if (busy) return
      busy = true
      try {
        const to = new Date()
        const after = newest.current ? `&after=${newest.current}` : ''
        const r = await api<Lines>(`/api/admin/logs/?${filter}&from=${resolve(from, to).toISOString()}&to=${to.toISOString()}&limit=500${after}`, { signal: controller.signal })
        if (r.lines.length) setTail((prev) => ({ key, lines: [...r.lines, ...(prev.key === key ? prev.lines : [])].slice(0, KEEP) }))
      } catch {
        // A missed tick is caught up by the next: it asks from the newest line here.
      } finally {
        busy = false
      }
    }, 3000)
    return () => {
      clearInterval(t)
      controller.abort()
    }
  }, [live, ready, key, filter, from])

  const refresh = () => {
    setTail({ key: '', lines: [] })
    void lines.refetch()
    void volume.refetch()
  }
  const series = (volume.data?.series ?? []).map((s) => ({ ...s, name: levelNames[s.name] ?? s.name }))

  return (
    <>
      <PageHeader title="Logs" description="Every service's logs from Loki, newest first. Narrow them by container, level and text; Live adds lines as they are written." />
      <div className="grid gap-4">
        <div className="flex flex-wrap items-end gap-3">
          <div className="grid gap-1">
            <span className="text-xs text-muted-foreground">Time range</span>
            <RangeSelect value={from} onChange={(v) => set((p) => p.set('range', v))} />
          </div>
          <Pick
            label="Containers"
            options={names.data?.containers ?? []}
            chosen={containers.length ? containers : [ALL]}
            onChange={(v) =>
              set((p) => {
                p.delete('container')
                for (const c of v) if (c !== ALL) p.append('container', c)
              })
            }
            multi
            allValue={ALL}
            allLabel="All containers"
            error={names.error ? 'The containers could not be listed.' : null}
            empty="No container wrote logs in the last day."
          />
          <div className="grid gap-1">
            <span className="text-xs text-muted-foreground">Level</span>
            <Segmented label="Level" value={level} onChange={(v) => set((p) => (v === 'all' ? p.delete('level') : p.set('level', v)))} options={levels} />
          </div>
          <SearchBox value={search} onChange={(v) => set((p) => (v ? p.set('q', v) : p.delete('q')))} />
          <div className="flex gap-2">
            <Button variant="outline" size="sm" className="h-9" onClick={refresh}>
              <RefreshCw /> Refresh
            </Button>
            <Button variant={live ? 'default' : 'outline'} size="sm" className="h-9" aria-pressed={live} onClick={() => setLive(!live)}>
              <Radio className={cn(live && 'animate-pulse')} /> Live
            </Button>
          </div>
        </div>

        <Card>
          <CardContent className="pt-4">
            {volume.error ? (
              <QueryError error={volume.error} retry={() => volume.refetch()} />
            ) : volume.isPending ? (
              <Skeleton className="h-36" />
            ) : (
              <TimeChart series={series} bars stacked height={140} fixed={levelColors} label="Log lines per interval, by level" from={volume.data.from} to={volume.data.to} />
            )}
          </CardContent>
        </Card>

        {lines.error ? (
          <QueryError error={lines.error} retry={() => lines.refetch()} />
        ) : lines.isPending ? (
          <Skeleton className="h-96" />
        ) : (
          <div className="grid min-w-0 gap-2">
            <LogsPanel lines={shown} label="Logs" tag="container" highlight={search} className="max-h-[70vh]" />
            {lines.data.more && (
              <p className="text-xs text-muted-foreground">
                The newest {lines.data.limit.toLocaleString('en-US')} lines of the range are here. Narrow the range or the filters to reach older ones.
              </p>
            )}
            <details className="min-w-0 text-xs text-muted-foreground">
              <summary className="w-fit cursor-pointer rounded-sm outline-none focus-visible:ring-[3px] focus-visible:ring-ring">The query sent to Loki</summary>
              <CodeBlock code={lines.data.query} label="LogQL" className="mt-2" />
            </details>
          </div>
        )}
      </div>
    </>
  )
}

/** The text to look for, applied as you stop typing. */
function SearchBox({ value, onChange }: { value: string; onChange: (v: string) => void }) {
  const id = useId()
  const [text, setText] = useState(value)
  useEffect(() => {
    if (text.trim() === value) return
    const t = setTimeout(() => onChange(text.trim()), 400)
    return () => clearTimeout(t)
  }, [text, value, onChange])
  return (
    <div className="grid gap-1">
      <Label htmlFor={id} className="text-xs text-muted-foreground">
        Contains
      </Label>
      <Input id={id} type="search" value={text} onChange={(e) => setText(e.target.value)} placeholder="Any text, any case" className="h-9 w-56" autoComplete="off" />
    </div>
  )
}
