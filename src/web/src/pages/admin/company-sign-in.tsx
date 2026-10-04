import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { KeyRound, PlugZap, Trash2 } from 'lucide-react'
import { useState } from 'react'
import { KeyValues } from '@/components/app/key-values'
import { Secret } from '@/components/app/secret'
import { Alert } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { useConfirm } from '@/components/ui/confirm'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { ago } from '@/lib/format'
import { companyStatusQuery, type CompanyStatus } from './company-api'

/** Below the Company sign-in settings: what to register at the identity provider, a test, and SCIM's token. */
export function CompanySignInPanel({ draft }: { draft: Record<string, string> }) {
  const status = useQuery(companyStatusQuery)
  const test = useMutation({
    mutationFn: () =>
      api<{ ok: boolean; message: string }>('/api/admin/company-sign-in/test', { body: Object.fromEntries(Object.entries(draft).filter(([k]) => k.startsWith('CompanySignIn:'))) }),
  })
  return (
    <div className="grid gap-4 pt-4">
      {status.data && (
        <KeyValues
          items={[
            ['Redirect URI to register', <code key="r" className="font-mono text-xs">{status.data.redirectUri}</code>],
            ['People who sign in so', String(status.data.people)],
          ]}
        />
      )}
      <div className="flex flex-wrap items-center gap-3">
        <Button variant="outline" onClick={() => test.mutate()} loading={test.isPending}>
          <PlugZap /> Test the identity provider
        </Button>
        <span className="text-sm text-muted-foreground">Reads its discovery document and keys, with the values above, saved or not.</span>
      </div>
      {test.data && <Alert variant={test.data.ok ? 'success' : 'destructive'}>{test.data.message}</Alert>}
      {test.error && <Alert variant="destructive">{errorMessage(test.error)}</Alert>}
      <ScimToken status={status.data} />
    </div>
  )
}

/** The one token the identity provider's SCIM client uses: shown once, replaced or revoked here. */
function ScimToken({ status }: { status?: CompanyStatus }) {
  const queryClient = useQueryClient()
  const confirm = useConfirm()
  const [token, setToken] = useState<string | null>(null)
  const refresh = () => queryClient.invalidateQueries({ queryKey: companyStatusQuery.queryKey })
  const make = useMutation({
    mutationFn: () => api<{ token: string }>('/api/admin/company-sign-in/scim-token', { body: {} }),
    onSuccess: (r) => {
      setToken(r.token)
      void refresh()
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const revoke = useMutation({
    mutationFn: () => api('/api/admin/company-sign-in/scim-token', { method: 'DELETE' }),
    onSuccess: () => {
      setToken(null)
      toast.success('SCIM is off: the token no longer works')
      void refresh()
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const madeAt = status?.scim.tokenMadeAt ?? null
  return (
    <section aria-labelledby="scim-title" className="grid gap-3 border-t pt-4">
      <div>
        <h3 id="scim-title" className="text-sm font-medium">
          SCIM provisioning
        </h3>
        <p className="mt-1 text-sm text-muted-foreground">
          The identity provider makes, changes and deactivates people and groups here. Give it this address and a token. Deactivating someone signs them out and blocks their API keys at once.
        </p>
      </div>
      {status && (
        <KeyValues
          items={[
            ['SCIM address', <code key="u" className="font-mono text-xs">{status.scim.url}</code>],
            ['Token', madeAt ? `made ${ago(madeAt)}` : 'none: SCIM is off'],
          ]}
        />
      )}
      {token && (
        <Alert variant="success" title="Copy the token now">
          It is shown only this once; the app keeps only its hash.
          <div className="mt-3">
            <Secret label="SCIM token" value={token} />
          </div>
        </Alert>
      )}
      <div className="flex flex-wrap gap-2">
        <Button
          variant="outline"
          loading={make.isPending}
          onClick={async () => {
            if (
              !madeAt ||
              (await confirm({ title: 'Make a new SCIM token?', description: 'The current token stops working at once: give the new one to the identity provider.', confirm: 'Make a new token' }))
            )
              make.mutate()
          }}
        >
          <KeyRound /> {madeAt ? 'Make a new token' : 'Make a token'}
        </Button>
        {madeAt && (
          <Button
            variant="outline"
            className="text-destructive-ink"
            loading={revoke.isPending}
            onClick={async () => {
              if (await confirm({ title: 'Turn SCIM off?', description: 'The token stops working at once. People and groups SCIM made stay as they are.', confirm: 'Turn off', destructive: true }))
                revoke.mutate()
            }}
          >
            <Trash2 /> Turn off
          </Button>
        )}
      </div>
    </section>
  )
}
