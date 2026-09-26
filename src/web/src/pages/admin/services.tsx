import { CheckCircle2, XCircle } from 'lucide-react'
import type { Probe } from './ops-api'

/** Each service: up or down, in words and an icon, with what it does and what the probe saw. */
export function ServiceList({ services }: { services: Probe[] }) {
  return (
    <ul className="grid divide-y rounded-lg border">
      {services.map((s) => (
        <li key={s.name} className="flex items-start gap-3 px-4 py-3">
          {s.ok ? <CheckCircle2 className="mt-0.5 size-4 shrink-0 text-success" aria-hidden="true" /> : <XCircle className="mt-0.5 size-4 shrink-0 text-destructive" aria-hidden="true" />}
          <div className="min-w-0 flex-1">
            <p className="font-medium">
              {s.name} <span className="sr-only">{s.ok ? 'up' : 'down'}</span>
            </p>
            <p className="text-sm text-muted-foreground">{s.purpose}</p>
          </div>
          <span className={`max-w-[45%] text-right text-sm break-words ${s.ok ? 'text-muted-foreground' : 'text-destructive-ink'}`}>
            {s.ok ? 'Up' : 'Down'} · {s.detail}
          </span>
        </li>
      ))}
    </ul>
  )
}
