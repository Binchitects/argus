import { useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { useSearchParams } from 'react-router'
import { api, ApiError } from '../api'

type Answer = { status: 'ok'; redirect: string } | { status: '2fa' }

/** Sign-in for the app and for everything that trusts it (Grafana, chat, the admin panel...). */
export function Login() {
  const [params] = useSearchParams()
  const redirect = params.get('rd') ?? '/'
  const queryClient = useQueryClient()
  const [step, setStep] = useState<'password' | '2fa'>('password')
  const [recovery, setRecovery] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const form = new FormData(e.currentTarget)
    setBusy(true)
    setError(null)
    try {
      const remember = form.get('remember') === 'on'
      const answer =
        step === 'password'
          ? await api<Answer>('/api/auth/login', {
              body: { userName: form.get('userName'), password: form.get('password'), remember, redirect },
            })
          : await api<Answer>('/api/auth/login/2fa', { body: { code: form.get('code'), recovery, remember, redirect } })
      if (answer.status === '2fa') {
        setStep('2fa')
      } else {
        await queryClient.invalidateQueries({ queryKey: ['me'] })
        // A full navigation: the target may be another service or the OIDC endpoint.
        window.location.assign(answer.redirect)
      }
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Sign-in failed. Try again.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <main className="login">
      <form className="card login-card" onSubmit={submit} aria-labelledby="login-title">
        <h1 id="login-title">Sign in</h1>
        {step === 'password' ? (
          <>
            <label>
              Username or email
              <input name="userName" autoComplete="username" required autoFocus />
            </label>
            <label>
              Password
              <input name="password" type="password" autoComplete="current-password" required />
            </label>
            <label className="check">
              <input name="remember" type="checkbox" /> Keep me signed in on this device
            </label>
          </>
        ) : (
          <>
            <p className="muted">
              {recovery ? 'Enter one of your recovery codes.' : 'Enter the 6-digit code from your authenticator app.'}
            </p>
            <label>
              {recovery ? 'Recovery code' : 'Code'}
              <input
                key={recovery ? 'recovery' : 'totp'}
                name="code"
                autoComplete="one-time-code"
                inputMode={recovery ? 'text' : 'numeric'}
                required
                autoFocus
              />
            </label>
            <button type="button" className="link" onClick={() => setRecovery(!recovery)}>
              {recovery ? 'Use the authenticator app instead' : 'Lost your phone? Use a recovery code'}
            </button>
          </>
        )}
        {error && (
          <p className="error" role="alert">
            {error}
          </p>
        )}
        <button className="button" disabled={busy}>
          {busy ? 'Signing in…' : step === 'password' ? 'Sign in' : 'Verify'}
        </button>
      </form>
    </main>
  )
}
