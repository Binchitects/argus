import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { RefreshCw, Settings } from 'lucide-react'
import type { ReactNode } from 'react'
import { Link } from 'react-router'
import { KeyValues } from '@/components/app/key-values'
import { PageHeader } from '@/components/app/page-header'
import { PageSkeleton, QueryError } from '@/components/app/query-state'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardFooter, CardHeader, CardTitle } from '@/components/ui/card'
import { api, errorMessage } from '@/lib/api'
import { companyStatusQuery } from './company-api'

interface SignIn {
  ldap: boolean
  ldapUrl: string | null
  adminGroup: string | null
  requiredGroup: string | null
  syncMinutes: number
}

export function SignInPage() {
  const settings = useQuery({ queryKey: ['admin', 'sign-in'], queryFn: ({ signal }) => api<SignIn>('/api/admin/sign-in', { signal }) })
  const company = useQuery(companyStatusQuery)
  const queryClient = useQueryClient()
  const sync = useMutation({
    mutationFn: () => api<{ checked: number; disabled: number }>('/api/admin/ldap/sync', { body: {} }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['admin', 'people'] }),
  })
  const configure = (
    <Button variant="outline" asChild>
      <Link to="/admin/settings#company-directory-ldap">
        <Settings /> Configure
      </Link>
    </Button>
  )
  if (settings.isPending) return <PageSkeleton />
  if (settings.error) return <QueryError error={settings.error} retry={() => settings.refetch()} />
  const s = settings.data
  return (
    <>
      <PageHeader title="Sign-in" description="Who can sign in, and how. The rules and limits are under Settings." />
      <div className="grid gap-6 lg:grid-cols-2">
        <Card>
          <CardHeader>
            <CardTitle className="flex items-center gap-2">
              Local accounts <Badge variant="success">On</Badge>
            </CardTitle>
            <CardDescription>Admins add people under People. Passwords are checked for strength, two-factor sign-in is available to everyone, and repeated wrong passwords lock the account for a while.</CardDescription>
          </CardHeader>
          <CardFooter>
            <Button variant="outline" asChild>
              <Link to="/admin/settings#sign-in-and-sessions">
                <Settings /> Sessions and limits
              </Link>
            </Button>
          </CardFooter>
        </Card>
        <Card>
          <CardHeader>
            <CardTitle className="flex items-center gap-2">
              Company directory (LDAP) {s.ldap ? <Badge variant="success">On</Badge> : <Badge variant="secondary">Off</Badge>}
            </CardTitle>
            <CardDescription>{s.ldap ? 'People sign in with their directory account; the app creates them on first sign-in.' : 'Off. Set it up under Settings; local accounts keep working next to it.'}</CardDescription>
          </CardHeader>
          {s.ldap && (
            <CardContent className="grid gap-4">
              <KeyValues
                items={[
                  ['Server', <code key="u" className="font-mono text-xs">{s.ldapUrl}</code>],
                  ['Admins are members of', s.adminGroup || 'nobody from the directory is an admin'],
                  ['Who may sign in', s.requiredGroup ? `members of ${s.requiredGroup}` : 'everyone in the directory'],
                  ['Checked', `every ${s.syncMinutes} minutes: people who left are disabled, people who return are enabled`],
                ]}
              />
              {sync.data && (
                <Alert variant="success">
                  Checked {sync.data.checked} {sync.data.checked === 1 ? 'person' : 'people'}, disabled {sync.data.disabled}.
                </Alert>
              )}
              {sync.error && <Alert variant="destructive">{errorMessage(sync.error)}</Alert>}
            </CardContent>
          )}
          <CardFooter className="flex-wrap">
            {s.ldap && (
              <Button onClick={() => sync.mutate()} loading={sync.isPending}>
                <RefreshCw /> Check the directory now
              </Button>
            )}
            {configure}
          </CardFooter>
        </Card>
        <Card>
          <CardHeader>
            <CardTitle className="flex items-center gap-2">
              Company sign-in (OIDC) {company.data?.enabled ? <Badge variant="success">On</Badge> : <Badge variant="secondary">Off</Badge>}
            </CardTitle>
            <CardDescription>
              {company.data?.enabled
                ? `The sign-in page offers "Sign in with ${company.data.label}". People are made at their first sign-in, or matched by email; the identity provider does their two-factor sign-in.`
                : 'Entra ID, Okta, Keycloak, Google or GitLab. Off: set it up under Settings; local accounts keep working next to it.'}
            </CardDescription>
          </CardHeader>
          {company.data && (
            <CardContent>
              <KeyValues
                items={[
                  ...(company.data.enabled
                    ? ([
                        ['Identity provider', <code key="i" className="font-mono text-xs">{company.data.issuer}</code>],
                        ['Admins are members of', company.data.adminGroup || 'nobody from the provider is an admin'],
                        ['Who may sign in', company.data.requiredGroup ? `members of ${company.data.requiredGroup}` : 'everyone the provider lets through'],
                      ] as [ReactNode, ReactNode][])
                    : []),
                  ['SCIM provisioning', company.data.scim.tokenMadeAt ? `on: ${company.data.scim.url}` : 'off'],
                ]}
              />
            </CardContent>
          )}
          <CardFooter>
            <Button variant="outline" asChild>
              <Link to="/admin/settings#company-sign-in">
                <Settings /> Configure
              </Link>
            </Button>
          </CardFooter>
        </Card>
      </div>
    </>
  )
}
