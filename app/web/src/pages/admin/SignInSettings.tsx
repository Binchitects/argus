import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from '../../api'
import { ErrorText } from '../../components/ErrorText'

interface Settings {
  ldap: boolean
  ldapUrl: string | null
  adminGroup: string | null
  requiredGroup: string | null
  syncMinutes: number
}

export function SignInSettings() {
  const settings = useQuery({ queryKey: ['admin', 'sign-in'], queryFn: () => api<Settings>('/api/admin/sign-in') })
  const queryClient = useQueryClient()
  const sync = useMutation({
    mutationFn: () => api<{ checked: number; disabled: number }>('/api/admin/ldap/sync', { body: {} }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['admin', 'people'] }),
  })
  const s = settings.data
  return (
    <section className="card">
      <h2>Sign-in</h2>
      <ErrorText error={settings.error} />
      {s && (
        <dl className="facts">
          <dt>Local accounts</dt>
          <dd>On. Admins add people here; passwords are checked for strength.</dd>
          <dt>Company directory (LDAP)</dt>
          <dd>{s.ldap ? `On: ${s.ldapUrl}` : 'Off. Set LDAP_URL and friends in .env to turn it on.'}</dd>
          {s.ldap && (
            <>
              <dt>Admins are members of</dt>
              <dd>{s.adminGroup || '— (nobody from the directory is an admin)'}</dd>
              <dt>Who may sign in</dt>
              <dd>{s.requiredGroup ? `members of ${s.requiredGroup}` : 'everyone in the directory'}</dd>
              <dt>Directory check</dt>
              <dd>every {s.syncMinutes} minutes: people who left are disabled, people who return are enabled</dd>
            </>
          )}
        </dl>
      )}
      {s?.ldap && (
        <>
          <button className="button secondary" onClick={() => sync.mutate()} disabled={sync.isPending}>
            Check the directory now
          </button>
          {sync.data && (
            <p role="status">
              Checked {sync.data.checked} people, disabled {sync.data.disabled}.
            </p>
          )}
          <ErrorText error={sync.error} />
        </>
      )}
    </section>
  )
}
