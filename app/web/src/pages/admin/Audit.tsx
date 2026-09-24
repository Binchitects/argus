import { useQuery } from '@tanstack/react-query'
import { api } from '../../api'
import { ErrorText } from '../../components/ErrorText'
import { when } from '../../format'

interface Event {
  id: number
  at: string
  actor: string | null
  action: string
  target: string | null
  success: boolean
  ip: string | null
  detail: string | null
}

export function Audit() {
  const events = useQuery({ queryKey: ['admin', 'audit'], queryFn: () => api<Event[]>('/api/admin/audit?take=300') })
  return (
    <section className="card">
      <h2>Audit log</h2>
      <p className="muted">Sign-ins and every change to people, newest first.</p>
      <ErrorText error={events.error} />
      <div className="table-wrap">
        <table>
          <thead>
            <tr>
              <th>When</th>
              <th>Who</th>
              <th>What</th>
              <th>To whom</th>
              <th>From</th>
              <th>Detail</th>
            </tr>
          </thead>
          <tbody>
            {events.data?.map((e) => (
              <tr key={e.id} className={e.success ? undefined : 'failed'}>
                <td>{when(e.at)}</td>
                <td>{e.actor ?? '—'}</td>
                <td>
                  {e.action}
                  {!e.success && <span className="badge warn">failed</span>}
                </td>
                <td>{e.target ?? '—'}</td>
                <td>{e.ip ?? '—'}</td>
                <td>{e.detail ?? ''}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  )
}
