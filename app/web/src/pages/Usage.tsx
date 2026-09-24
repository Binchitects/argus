import { useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { useOutletContext } from 'react-router'
import { api, type Me } from '../api'
import { TimeChart } from '../charts/TimeChart'
import { ErrorText } from '../components/ErrorText'
import { DashboardView } from '../dashboards/DashboardView'
import { formatValue } from '../format'
import { intervalFor, presets, resolve } from '../time'

export function Usage() {
  const me = useOutletContext<Me>()
  const [tab, setTab] = useState<'mine' | 'everyone'>(me.isAdmin ? 'everyone' : 'mine')
  return (
    <>
      <h1>Usage &amp; cost</h1>
      <p className="lede">Tokens and cost. Input is split into cache miss and cache hit, which are priced differently.</p>
      {me.isAdmin && (
        <div className="tabs" role="tablist" aria-label="Whose usage">
          <button role="tab" type="button" aria-selected={tab === 'everyone'} className={tab === 'everyone' ? 'active' : ''} onClick={() => setTab('everyone')}>
            Everyone
          </button>
          <button role="tab" type="button" aria-selected={tab === 'mine'} className={tab === 'mine' ? 'active' : ''} onClick={() => setTab('mine')}>
            Mine
          </button>
        </div>
      )}
      {tab === 'everyone' && me.isAdmin ? <DashboardView uid="usage-by-user" /> : <MyUsage />}
    </>
  )
}

interface Mine {
  intervalMs: number
  totals: { requests: number; inputMiss: number; inputHit: number; output: number; cost: number }
  series: { name: string; points: (number | null)[][] }[]
  models: { model: string; requests: number; tokens: number; cost: number }[]
}

function MyUsage() {
  const [from, setFrom] = useState('now-30d')
  const data = useQuery({
    queryKey: ['usage', 'me', from],
    queryFn: async () => {
      const f = resolve(from)
      const t = new Date()
      const q = new URLSearchParams({ from: f.toISOString(), to: t.toISOString(), intervalMs: String(Math.max(intervalFor(f, t, 60), 3_600_000)) })
      return { ...(await api<Mine>(`/api/usage/me?${q}`)), from: f.getTime(), to: t.getTime() }
    },
  })
  const t = data.data?.totals
  const prompt = t ? t.inputMiss + t.inputHit : 0
  const tokens = data.data?.series.filter((s) => s.name !== 'cost') ?? []
  const cost = data.data?.series.filter((s) => s.name === 'cost').map((s) => ({ ...s, name: 'Cost' })) ?? []
  return (
    <div className="stack">
      <div className="toolbar" role="group" aria-label="Time range">
        {presets.map((p) => (
          <button key={p.from} type="button" className={`chip${from === p.from ? ' active' : ''}`} aria-pressed={from === p.from} onClick={() => setFrom(p.from)}>
            {p.label}
          </button>
        ))}
      </div>
      <ErrorText error={data.error} />
      {t && (
        <>
          <div className="tiles">
            <Tile label="Requests" value={formatValue(t.requests)} />
            <Tile label="Input, cache miss" value={formatValue(t.inputMiss)} />
            <Tile label="Input, cache hit" value={formatValue(t.inputHit)} hint={prompt ? `${formatValue((100 * t.inputHit) / prompt, 'percent')} of input` : undefined} />
            <Tile label="Output" value={formatValue(t.output)} />
            <Tile label="Cost" value={formatValue(t.cost, 'currencyUSD')} />
          </div>
          <section className="panel">
            <h3>Tokens by kind</h3>
            {tokens.length ? <TimeChart series={tokens} stacked bars label="Your tokens by kind over time" from={data.data?.from} to={data.data?.to} /> : <p className="muted">No requests in this time range.</p>}
          </section>
          {cost.length > 0 && (
            <section className="panel">
              <h3>Cost</h3>
              <TimeChart series={cost} unit="currencyUSD" bars label="Your cost over time" from={data.data?.from} to={data.data?.to} />
            </section>
          )}
          {data.data!.models.length > 0 && (
            <section className="panel">
              <h3>By model</h3>
              <div className="table-wrap">
                <table>
                  <thead>
                    <tr>
                      <th>Model</th>
                      <th className="num">Requests</th>
                      <th className="num">Tokens</th>
                      <th className="num">Cost</th>
                    </tr>
                  </thead>
                  <tbody>
                    {data.data!.models.map((m) => (
                      <tr key={m.model}>
                        <td>{m.model}</td>
                        <td className="num">{formatValue(m.requests)}</td>
                        <td className="num">{formatValue(m.tokens)}</td>
                        <td className="num">{formatValue(m.cost, 'currencyUSD')}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </section>
          )}
        </>
      )}
    </div>
  )
}

export function Tile({ label, value, hint }: { label: string; value: string; hint?: string }) {
  return (
    <div className="tile">
      <span className="tile-label">{label}</span>
      <span className="tile-value" aria-label={label}>
        {value}
      </span>
      {hint && <span className="muted">{hint}</span>}
    </div>
  )
}
