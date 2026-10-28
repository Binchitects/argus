import { useQuery, useQueryClient } from '@tanstack/react-query'
import { CircleUserRound } from 'lucide-react'
import { useState, type FormEvent } from 'react'
import { SendFeedbackDialog } from '@/components/app/send-feedback'
import { Button } from '@/components/ui/button'
import { useConfirm } from '@/components/ui/confirm'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuLabel, DropdownMenuSeparator, DropdownMenuTrigger } from '@/components/ui/dropdown-menu'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { toast } from '@/components/ui/toaster'
import { Tooltip } from '@/components/ui/tooltip'
import { errorMessage } from '@/lib/api'
import { api } from './http'

/** Whose key Code Arena works with, as Arena says it. */
export interface CodeAccount {
  arena: string | null
  signedIn: boolean
  /** A Code Studio workspace: it works with Arena's key for it, and does not sign in. */
  codeStudio: boolean
  account: { userName: string; displayName: string | null; email: string | null; managesPeople: boolean } | null
  error?: string
}

const accountQuery = { queryKey: ['code', 'account'] as const, queryFn: () => api<CodeAccount>('/api/account') }

const open = (url: string) => window.open(url, '_blank', 'noopener,noreferrer')

/** After a sign-in: Code Arena starts again with the new key; the page loads once Arena says whose it is (within a minute). */
async function reloadWhenSignedIn() {
  for (let waited = 0; waited < 60_000; waited += 500) {
    await new Promise((r) => setTimeout(r, 500))
    try {
      if ((await api<CodeAccount>('/api/account')).account) {
        window.location.reload()
        return
      }
    } catch {
      // Between the two runs.
    }
  }
  toast.error('Code Arena did not start again with the new key: see its terminal.')
}

/** Sign in with a user name and password (and the second factor when Arena asks): Arena makes a key for this machine. */
function SignInDialog({ arena, onClose }: { arena: string | null; onClose: () => void }) {
  const [form, setForm] = useState({ url: arena ?? '', userName: '', password: '', code: '' })
  const [step, setStep] = useState<'password' | 'code' | 'starting'>('password')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const set = (k: keyof typeof form) => (e: { target: { value: string } }) => setForm({ ...form, [k]: e.target.value })
  const submit = async (e: FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setError(null)
    try {
      const said =
        step === 'password'
          ? await api<{ status: 'ok' | '2fa' }>('/api/account/sign-in', { body: { url: form.url, userName: form.userName, password: form.password } })
          : await api<{ status: 'ok' }>('/api/account/sign-in/code', { body: { code: form.code } })
      if (said.status === '2fa') {
        setStep('code')
        return
      }
      setStep('starting')
      await reloadWhenSignedIn()
    } catch (err) {
      setError(errorMessage(err))
    } finally {
      setBusy(false)
    }
  }
  return (
    <Dialog open onOpenChange={(o) => !o && step !== 'starting' && onClose()}>
      <DialogContent className="sm:max-w-md">
        <form onSubmit={(e) => void submit(e)} className="grid gap-4">
          <DialogHeader>
            <DialogTitle>Sign in to Arena</DialogTitle>
            <DialogDescription>With your user name and password, as on Arena's page. Arena makes a key of yours for this machine; Your account → API keys lists it.</DialogDescription>
          </DialogHeader>
          {step === 'password' && (
            <>
              <Field label="Arena's address">
                <Input type="url" value={form.url} onChange={set('url')} placeholder="https://arena.example.com" required />
              </Field>
              <Field label="User name">
                <Input value={form.userName} onChange={set('userName')} autoComplete="username" required autoFocus />
              </Field>
              <Field label="Password">
                <Input type="password" value={form.password} onChange={set('password')} autoComplete="current-password" required />
              </Field>
            </>
          )}
          {step === 'code' && (
            <Field label="Two-factor code" hint="From your authenticator app, or a recovery code.">
              <Input value={form.code} onChange={set('code')} autoComplete="one-time-code" inputMode="numeric" required autoFocus />
            </Field>
          )}
          {step === 'starting' && <p className="text-sm text-muted-foreground">Signed in: Code Arena starts again with the new key…</p>}
          {error && (
            <p role="alert" className="text-sm font-medium text-destructive">
              {error}
            </p>
          )}
          <DialogFooter>
            <Button type="button" variant="outline" onClick={onClose} disabled={step === 'starting'}>
              Cancel
            </Button>
            <Button type="submit" loading={busy || step === 'starting'}>
              {step === 'code' ? 'Verify' : 'Sign in'}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  )
}

/**
 * Accounts, at the activity bar's foot above the gear (as VS Code's): whose key Code Arena works with, signing in with a
 * user name and password, signing out, and Arena's own pages: your account, and People for those who may see it.
 */
export function AccountsMenu({ edge }: { edge: 'left' | 'right' }) {
  const queryClient = useQueryClient()
  const confirm = useConfirm()
  const account = useQuery(accountQuery)
  const [signingIn, setSigningIn] = useState(false)
  const [feedback, setFeedback] = useState(false)
  const a = account.data
  const who = a?.account ? (a.account.displayName ? `${a.account.displayName} (${a.account.userName})` : a.account.userName) : null
  const host = a?.arena ? new URL(a.arena).host : null
  const signOut = async () => {
    if (!(await confirm({ title: 'Sign out of Arena?', description: "The key Code Arena signed in with is revoked, and forgotten here; the model's answers stop until you sign in again.", confirm: 'Sign out', destructive: true })))
      return
    try {
      const said = await api<{ revoked: boolean }>('/api/account/sign-out', { body: {} })
      toast.success(said.revoked ? 'Signed out: its key is revoked.' : 'Signed out: the key is forgotten here (Account → API keys in Arena revokes it).')
      await queryClient.invalidateQueries({ queryKey: accountQuery.queryKey })
    } catch (e) {
      toast.error(errorMessage(e))
    }
  }
  return (
    <>
      <DropdownMenu>
        <Tooltip content={who ? `Accounts: ${who}` : 'Accounts'} side={edge === 'left' ? 'right' : 'left'}>
          <DropdownMenuTrigger asChild>
            <button
              type="button"
              aria-label="Accounts"
              className="relative grid size-12 place-items-center text-muted-foreground outline-none transition-colors hover:text-foreground focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-inset [&_svg]:size-5"
            >
              <CircleUserRound aria-hidden="true" />
              {a && (!a.signedIn || a.error) && <span className="absolute top-2.5 right-2.5 size-2 rounded-full bg-warning" aria-hidden="true" />}
            </button>
          </DropdownMenuTrigger>
        </Tooltip>
        <DropdownMenuContent side={edge === 'left' ? 'right' : 'left'} align="end" className="w-80 [&_[role=menuitem]]:whitespace-nowrap">
          <DropdownMenuLabel className="grid gap-0.5 font-normal">
            <span className="truncate text-sm font-medium">{a?.codeStudio ? "Code Studio's workspace" : who ? who : a?.signedIn ? 'Signed in' : 'Not signed in'}</span>
            {host && <span className="truncate text-xs text-muted-foreground">on {host}</span>}
            {a?.error && <span className="text-xs text-warning-ink">{a.error}</span>}
          </DropdownMenuLabel>
          <DropdownMenuSeparator />
          {!a?.codeStudio && <DropdownMenuItem onSelect={() => setSigningIn(true)}>{a?.signedIn ? 'Sign in as someone else…' : 'Sign in with user name and password…'}</DropdownMenuItem>}
          {a?.arena && <DropdownMenuItem onSelect={() => open(`${a.arena!.replace(/\/$/, '')}/account`)}>Your account in Arena</DropdownMenuItem>}
          {a?.arena && a.account?.managesPeople && <DropdownMenuItem onSelect={() => open(`${a.arena!.replace(/\/$/, '')}/admin/people`)}>People (Admin)</DropdownMenuItem>}
          {a?.signedIn && <DropdownMenuItem onSelect={() => setFeedback(true)}>Send feedback…</DropdownMenuItem>}
          {!a?.codeStudio && a?.signedIn && (
            <>
              <DropdownMenuSeparator />
              <DropdownMenuItem onSelect={() => void signOut()}>Sign out</DropdownMenuItem>
            </>
          )}
        </DropdownMenuContent>
      </DropdownMenu>
      {signingIn && <SignInDialog arena={a?.arena ?? null} onClose={() => setSigningIn(false)} />}
      {feedback && <SendFeedbackDialog page="the IDE" send={(body) => api('/api/account/feedback', { body })} onClose={() => setFeedback(false)} />}
    </>
  )
}
