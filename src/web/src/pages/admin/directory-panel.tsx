import { useMutation } from '@tanstack/react-query'
import { AlertTriangle, BookOpen, CheckCircle2, ChevronRight, PlugZap, UserCheck, XCircle } from 'lucide-react'
import { useState } from 'react'
import { Alert } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { api, errorMessage } from '@/lib/api'

/** A directory check's answer: whether it works, the one thing to say first, and each step. */
export interface DirectoryCheck {
  ok: boolean
  message: string
  steps: { state: 'ok' | 'warn' | 'fail'; text: string }[]
}

/** The form's directory values, saved or not: what the checks try. */
const directoryValues = (draft: Record<string, string>) => Object.fromEntries(Object.entries(draft).filter(([k]) => k.startsWith('Ldap:')))

/** One example of each field, for OpenLDAP and for Active Directory; `values` are shown as code. */
const examples: [string, string, string][] = [
  ['Directory server', '`ldap://ldap.example.com:389` with Use StartTLS, or `ldaps://ldap.example.com:636`', '`ldaps://dc1.corp.example.com:636`'],
  ["Directory's CA", 'the CA of its certificate, in PEM, when it is your own', "your enterprise CA's root certificate, in PEM"],
  ['Service account', '`cn=readonly,dc=example,dc=com`', '`reader@corp.example.com` or `CORP\\reader`'],
  ['Where people are', '`ou=people,dc=example,dc=com`', '`OU=Staff,DC=corp,DC=example,DC=com`'],
  ['Which entries are people', 'the default: it finds `uid` and `mail`', 'the default: it finds `sAMAccountName`, `userPrincipalName` and `mail`'],
  ['Where groups are', '`ou=groups,dc=example,dc=com`', 'empty: `memberOf` is always there'],
  ['Admin group', '`llm-admins`', '`LLM Admins`'],
  ['Required group', '`llm-users`, or empty for everyone', '`LLM Users`, or empty for everyone'],
]

/** Text with `values` in code. */
function Example({ text }: { text: string }) {
  return text.split('`').map((part, i) => (i % 2 ? <code key={i} className="font-mono break-all text-foreground">{part}</code> : part))
}

/**
 * At the top of the Company directory section: how to set it up, step by step, with an example
 * of each field for OpenLDAP and for Active Directory. Open while no directory is set.
 */
export function DirectoryGuide({ open }: { open: boolean }) {
  // Opened or closed as the page opens; then as the admin leaves it.
  const [initiallyOpen] = useState(open)
  return (
    <details open={initiallyOpen} className="group pb-4">
      <summary className="flex cursor-pointer items-center gap-2 rounded-md text-sm font-medium outline-none select-none focus-visible:ring-[3px] focus-visible:ring-ring [&::-webkit-details-marker]:hidden">
        <ChevronRight className="size-4 text-muted-foreground transition-transform group-open:rotate-90" aria-hidden="true" />
        <BookOpen className="size-4 text-muted-foreground" aria-hidden="true" />
        How to set it up, step by step
      </summary>
      <div className="mt-3 grid gap-4 text-sm">
        <p className="text-muted-foreground">
          People then sign in here with their directory username (uid on OpenLDAP, sAMAccountName on Active Directory), their email, or user@domain and DOMAIN\user.
          The directory checks their password; nothing of it is kept here.
        </p>
        <ol className="ml-5 list-decimal space-y-1.5 text-muted-foreground">
          <li>
            Ask your directory team for the server's address and a <span className="text-foreground">read-only service account</span> that can search people and groups.
            On Active Directory, a plain user account with "Password never expires" ticked and "User must change password at next logon" unticked.
          </li>
          <li>
            <span className="text-foreground">Directory server</span>: ldaps:// on port 636, or ldap:// on port 389 with Use StartTLS on. If its certificate comes from your company's own CA, paste that CA in
            Directory's CA.
          </li>
          <li>
            <span className="text-foreground">Service account</span> and its password, then <span className="text-foreground">Where people are</span>: the DN that people are below.
          </li>
          <li>
            <span className="text-foreground">Admin group</span>, whose members are admins here, and, if only some people may use the app, a <span className="text-foreground">Required group</span>. If the
            test says people's groups are not in their memberOf, fill in Where groups are.
          </li>
          <li>
            <span className="text-foreground">Test the settings</span> below: each step says what works and, when something does not, exactly what to fix. Testing saves nothing.
          </li>
          <li>
            <span className="text-foreground">Try a person's sign-in</span> with someone's username and password: it shows who they would be here, or why they could not sign in.
          </li>
          <li>
            Save. The settings apply at once, with no restart. Keep a local admin account as a way in for when the directory is down.
          </li>
        </ol>
        <div className="overflow-x-auto">
          <table className="w-full min-w-[28rem] text-left text-xs">
            <caption className="sr-only">An example of each field</caption>
            <thead className="text-muted-foreground">
              <tr className="border-b">
                <th scope="col" className="py-1.5 pr-3 font-medium">Field</th>
                <th scope="col" className="py-1.5 pr-3 font-medium">OpenLDAP</th>
                <th scope="col" className="py-1.5 font-medium">Active Directory</th>
              </tr>
            </thead>
            <tbody>
              {examples.map(([field, openldap, ad]) => (
                <tr key={field} className="border-b last:border-0">
                  <th scope="row" className="py-1.5 pr-3 align-top font-normal text-muted-foreground">{field}</th>
                  <td className="py-1.5 pr-3 align-top">
                    <Example text={openldap} />
                  </td>
                  <td className="py-1.5 align-top">
                    <Example text={ad} />
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        <p className="text-muted-foreground">
          <Example text="On the osixia/openldap image, the service account is `cn=readonly,dc=example,dc=org` (with LDAP_READONLY_USER=true) or `cn=admin,dc=example,dc=org`, with its own base DN (from LDAP_DOMAIN) in place of `dc=example,dc=org`. For StartTLS and ldaps:// it needs LDAP_TLS_VERIFY_CLIENT=try (else it demands a certificate from the app, which has none) and a certificate of your own (the image's own CA expired on 2026-01-15). With groupOfNames groups, fill in Where groups are." />
        </p>
      </div>
    </details>
  )
}

/** Below the directory settings: test them, and try a person's sign-in, with the values above, saved or not. */
export function DirectoryChecks({ draft }: { draft: Record<string, string> }) {
  const test = useMutation({
    mutationFn: () => api<DirectoryCheck>('/api/admin/config/ldap-test', { body: directoryValues(draft) }),
  })
  const [login, setLogin] = useState('')
  const [password, setPassword] = useState('')
  const attempt = useMutation({
    mutationFn: () => api<DirectoryCheck>('/api/admin/config/ldap-try', { body: { settings: directoryValues(draft), login, password } }),
  })
  return (
    <div className="grid gap-4 pt-4">
      <div className="flex flex-wrap items-center gap-3">
        <Button variant="outline" onClick={() => test.mutate()} loading={test.isPending}>
          <PlugZap /> Test the settings
        </Button>
        <span className="text-sm text-muted-foreground">
          Tries the values above, saved or not: the server, its certificate, the service account, where people are and the groups. A blank password field uses the saved password, but only with the saved server and service account, over a connection as safe as the saved one.
        </span>
      </div>
      {test.data && <CheckResult result={test.data} />}
      {test.error && <Alert variant="destructive">{errorMessage(test.error)}</Alert>}

      <section aria-labelledby="ldap-try-title" className="grid gap-3 border-t pt-4">
        <div>
          <h3 id="ldap-try-title" className="text-sm font-medium">
            Try a person's sign-in
          </h3>
          <p className="mt-1 text-sm text-muted-foreground">
            Checks a username and password the way signing in does, with the values above, and shows who they would be here, or exactly why they could not sign in. Nothing is
            saved or changed, and the password is neither kept nor logged; the try is in the audit log. A wrong password counts as a wrong sign-in does.
          </p>
        </div>
        <form
          className="grid gap-3 sm:grid-cols-[minmax(0,1fr)_minmax(0,1fr)_auto] sm:items-end"
          onSubmit={(e) => {
            e.preventDefault()
            attempt.mutate()
          }}
        >
          <Field label="Their username">
            <Input value={login} onChange={(e) => setLogin(e.target.value)} autoComplete="off" spellCheck={false} placeholder="jsmith, jsmith@example.com or CORP\jsmith" />
          </Field>
          <Field label="Their password">
            <Input type="password" value={password} onChange={(e) => setPassword(e.target.value)} autoComplete="new-password" />
          </Field>
          <Button type="submit" variant="outline" loading={attempt.isPending} disabled={!login.trim() || !password}>
            <UserCheck /> Try it
          </Button>
        </form>
        {attempt.data && <CheckResult result={attempt.data} />}
        {attempt.error && <Alert variant="destructive">{errorMessage(attempt.error)}</Alert>}
      </section>
    </div>
  )
}

const stepLook = {
  ok: { icon: CheckCircle2, className: 'text-success', label: 'Done' },
  warn: { icon: AlertTriangle, className: 'text-warning', label: 'Warning' },
  fail: { icon: XCircle, className: 'text-destructive', label: 'Failed' },
} as const

/** The answer first, then each step with its state in an icon and in words (the failed one is the answer: not said twice). */
function CheckResult({ result }: { result: DirectoryCheck }) {
  const warned = result.steps.some((s) => s.state === 'warn')
  const steps = result.steps.filter((s) => !(s.state === 'fail' && s.text === result.message))
  return (
    // Messages hold long unbroken filters and DNs: they wrap anywhere rather than widen the page.
    <Alert variant={!result.ok ? 'destructive' : warned ? 'warning' : 'success'} title={result.message} className="[&_p]:break-words">
      {steps.length > 0 && (
        <ol className="mt-2 grid gap-1.5" aria-label={result.ok ? 'Steps' : 'Steps before it'}>
          {steps.map((s, i) => {
            const look = stepLook[s.state]
            return (
              <li key={i} className="grid grid-cols-[auto_1fr] gap-2">
                <look.icon className={`mt-0.5 size-4 ${look.className}`} aria-hidden="true" />
                <span className="min-w-0 break-words text-foreground">
                  <span className="sr-only">{look.label}: </span>
                  {s.text}
                </span>
              </li>
            )
          })}
        </ol>
      )}
    </Alert>
  )
}
