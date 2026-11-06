// A VM's sign-in, asked for by the agent's VM tools (vm_run, vm_copy, vm_test): one question at a time, shown in a dialog.

import { api } from './http'

/** What code-arena web asks: the VM, its hypervisor, the user it had, whether the guest refused it, and where it could be kept. */
export interface VmLoginQuestion {
  id: string
  vm: string
  hypervisor: string
  user: string | null
  refused: boolean
  canSave: boolean
  keyring: string
}

let current: VmLoginQuestion | null = null
const listeners = new Set<() => void>()
const changed = () => listeners.forEach((l) => l())

export const askedLogin = () => current
export function onLoginAsk(f: () => void) {
  listeners.add(f)
  return () => {
    listeners.delete(f)
  }
}

/** A question from the turn's events (vm_login), or its end (vm_login_done: answered, stopped, or asked in another tab). */
export function loginEvent(e: { type: string; id?: string } & Partial<VmLoginQuestion>) {
  if (e.type === 'vm_login' && e.id && e.vm) {
    current = { id: e.id, vm: e.vm, hypervisor: e.hypervisor ?? '', user: e.user ?? null, refused: !!e.refused, canSave: !!e.canSave, keyring: e.keyring ?? '' }
    changed()
  } else if (e.type === 'vm_login_done' && current?.id === e.id) {
    current = null
    changed()
  }
}

/** The person's answer; the password goes to code-arena web only, which hands it to the hypervisor. */
export async function answerLogin(id: string, answer: { user: string; password: string; save: boolean } | 'cancel') {
  await api('/api/vm-login', { body: answer === 'cancel' ? { id, cancel: true } : { id, ...answer } })
  if (current?.id === id) {
    current = null
    changed()
  }
}
