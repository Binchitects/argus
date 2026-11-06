import { act, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { api } from './http'
import { loginEvent } from './vm-login'
import { VmLoginDialog } from './vm-login-dialog'

vi.mock('./http', () => ({ api: vi.fn(), ApiError: class ApiError extends Error {} }))
vi.mock('@/components/ui/toaster', () => ({ toast: { info: vi.fn(), error: vi.fn() } }))

const question = { type: 'vm_login', id: 'login_1', vm: 'test-vm', hypervisor: 'VirtualBox', user: 'tester', refused: false, canSave: true, keyring: 'the keyring (Secret Service)' }

describe("a VM's sign-in", () => {
  beforeEach(() => {
    vi.mocked(api).mockReset()
    vi.mocked(api).mockResolvedValue(undefined)
  })

  it('takes the user it had and an unseen password, kept in the keyring when asked, and closes', async () => {
    render(<VmLoginDialog />)
    act(() => loginEvent(question))
    const dialog = await screen.findByRole('dialog', { name: 'Sign in to test-vm' })
    expect(within(dialog).getByLabelText('User in the guest')).toHaveValue('tester')
    const password = within(dialog).getByLabelText('Password')
    expect(password).toHaveAttribute('type', 'password')
    expect(password).toHaveFocus()
    await userEvent.type(password, 'pa55word')
    await userEvent.click(within(dialog).getByRole('checkbox', { name: 'Keep it in the keyring (Secret Service)' }))
    await userEvent.click(within(dialog).getByRole('button', { name: 'Sign in' }))
    expect(api).toHaveBeenCalledWith('/api/vm-login', { body: { id: 'login_1', user: 'tester', password: 'pa55word', save: true } })
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
  })

  it('says the guest refused the last one, offers no keyring where there is none, and cancels', async () => {
    render(<VmLoginDialog />)
    act(() => loginEvent({ ...question, id: 'login_2', user: null, refused: true, canSave: false }))
    const dialog = await screen.findByRole('dialog', { name: 'Sign in to test-vm' })
    expect(dialog).toHaveTextContent('The guest refused the last sign-in.')
    expect(within(dialog).queryByRole('checkbox')).toBeNull()
    expect(within(dialog).getByRole('button', { name: 'Sign in' })).toBeDisabled()
    await userEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }))
    expect(api).toHaveBeenCalledWith('/api/vm-login', { body: { id: 'login_2', cancel: true } })
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
  })

  it('closes when the question ends elsewhere (stopped, or answered in another tab)', async () => {
    render(<VmLoginDialog />)
    act(() => loginEvent({ ...question, id: 'login_3' }))
    await screen.findByRole('dialog', { name: 'Sign in to test-vm' })
    act(() => loginEvent({ type: 'vm_login_done', id: 'login_3' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
    expect(api).not.toHaveBeenCalled()
  })
})
