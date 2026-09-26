import { useQuery } from '@tanstack/react-query'
import { useEffect, useId, useState } from 'react'
import { Pick } from '@/components/app/pick'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { api } from '@/lib/api'
import { resolve } from '@/lib/time'
import type { VariableDef } from './types'

export type Chosen = Record<string, string[]>

const ALL = '$__all'

/**
 * A dashboard's variables, as Grafana shows them above the panels: a list from
 * Prometheus or Loki (one or several, or All), or a text box.
 */
export function Variables({ uid, variables, from, to, value, onChange }: { uid: string; variables: VariableDef[]; from: string; to: string; value: Chosen; onChange: (v: Chosen) => void }) {
  const options = useQuery({
    queryKey: ['dashboard', uid, 'variables', from, to],
    queryFn: ({ signal }) => {
      // The range as times: "now-1h" means now, when the options are asked for.
      const now = new Date()
      const [start, end] = [resolve(from, now).toISOString(), resolve(to, now).toISOString()]
      return api<{ name: string; options: string[]; error: string | null }[]>(`/api/dashboards/${uid}/variables?from=${encodeURIComponent(start)}&to=${encodeURIComponent(end)}`, { signal })
    },
    enabled: variables.some((v) => v.type !== 'textbox'),
    staleTime: 60_000,
  })
  if (!variables.length) return null
  return (
    <fieldset className="flex flex-wrap items-end gap-3">
      <legend className="sr-only">Dashboard variables</legend>
      {variables.map((v) => {
        const chosen = value[v.name] ?? v.current
        const set = (values: string[]) => onChange({ ...value, [v.name]: values })
        if (v.type === 'textbox') return <TextVariable key={v.name} label={v.label} value={chosen[0] ?? ''} onChange={(t) => set([t])} />
        const found = options.data?.find((o) => o.name === v.name)
        return <ListVariable key={v.name} v={v} options={found?.options ?? []} error={found?.error ?? null} chosen={chosen} onChange={set} />
      })}
    </fieldset>
  )
}

/** A text box, applied as you stop typing. */
function TextVariable({ label, value, onChange }: { label: string; value: string; onChange: (v: string) => void }) {
  const id = useId()
  const [text, setText] = useState(value)
  useEffect(() => {
    if (text === value) return
    const t = setTimeout(() => onChange(text), 500)
    return () => clearTimeout(t)
  }, [text, value, onChange])
  return (
    <div className="grid gap-1">
      <Label htmlFor={id} className="text-xs text-muted-foreground">
        {label}
      </Label>
      <Input id={id} value={text} onChange={(e) => setText(e.target.value)} className="h-9 w-48" autoComplete="off" />
    </div>
  )
}

function ListVariable({ v, options, error, chosen, onChange }: { v: VariableDef; options: string[]; error: string | null; chosen: string[]; onChange: (values: string[]) => void }) {
  return (
    <Pick
      label={v.label}
      options={options}
      chosen={chosen}
      onChange={onChange}
      multi={v.multi}
      allValue={v.includeAll ? ALL : undefined}
      error={error}
      empty="Nothing in this time range."
    />
  )
}
