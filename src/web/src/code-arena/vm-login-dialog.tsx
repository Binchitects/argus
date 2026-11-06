import { useState, useSyncExternalStore, type FormEvent } from 'react'
import { toast } from '@/components/ui/toaster'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { errorMessage } from '@/lib/api'
import { answerLogin, askedLogin, onLoginAsk, type VmLoginQuestion } from './vm-login'

/** The guest's user and password for the agent's VM tools: given here, never shown in the chat. */
export function VmLoginDialog() {
  const ask = useSyncExternalStore(onLoginAsk, askedLogin)
  if (!ask) return null
  // A form of its own for each question: what was typed for one VM is not offered to the next.
  return <LoginForm key={ask.id} ask={ask} />
}

function LoginForm({ ask }: { ask: VmLoginQuestion }) {
  const [user, setUser] = useState(ask.user ?? '')
  const [password, setPassword] = useState('')
  const [save, setSave] = useState(false)
  const [busy, setBusy] = useState(false)
  const send = (answer: Parameters<typeof answerLogin>[1]) => {
    setBusy(true)
    answerLogin(ask.id, answer)
      .catch((e) => toast.error(errorMessage(e)))
      .finally(() => setBusy(false))
  }
  const submit = (e: FormEvent) => {
    e.preventDefault()
    if (user.trim()) send({ user: user.trim(), password, save })
  }
  return (
    <Dialog open onOpenChange={(o) => !o && !busy && send('cancel')}>
      <DialogContent className="max-w-md">
        <form onSubmit={submit} className="grid gap-4">
          <DialogHeader>
            <DialogTitle>Sign in to {ask.vm}</DialogTitle>
            <DialogDescription>
              {ask.refused ? 'The guest refused the last sign-in. ' : ''}
              The agent needs a user in this {ask.hypervisor} VM to run commands and copy files in it. The password goes to the hypervisor only: it is not in the chat, the session or the log.
            </DialogDescription>
          </DialogHeader>
          <Field label="User in the guest">
            <Input value={user} onChange={(e) => setUser(e.target.value)} autoComplete="off" autoFocus={!ask.user} spellCheck={false} />
          </Field>
          <Field label="Password">
            <Input type="password" value={password} onChange={(e) => setPassword(e.target.value)} autoComplete="off" autoFocus={!!ask.user} />
          </Field>
          {ask.canSave && (
            <div className="flex items-center gap-2">
              <Checkbox id="vm-login-save" checked={save} onCheckedChange={(c) => setSave(c === true)} />
              <Label htmlFor="vm-login-save" className="font-normal">
                Keep it in {ask.keyring}
              </Label>
            </div>
          )}
          <DialogFooter>
            <Button type="button" variant="outline" disabled={busy} onClick={() => send('cancel')}>
              Cancel
            </Button>
            <Button type="submit" disabled={busy || !user.trim()}>
              Sign in
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  )
}
