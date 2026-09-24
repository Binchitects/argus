import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import QRCode from 'qrcode'
import { useEffect, useState, type FormEvent } from 'react'
import { useOutletContext } from 'react-router'
import { api, type Me } from '../api'
import { ErrorText } from '../components/ErrorText'
import { OnceNotice, Secret } from '../components/Secret'
import { money, when } from '../format'

interface Keys {
  keys: { alias: string; preview: string | null; spend: number; blocked: boolean; createdAt: string | null }[]
  spend: number
  budget: number | null
}

export function Account() {
  const me = useOutletContext<Me>()
  return (
    <>
      <h1>Your account</h1>
      <p className="lede">
        {me.displayName} · {me.userName} · {me.email}
        {me.source === 'ldap' && ' · signs in through the company directory'}
      </p>
      <div className="stack">
        <ApiKeys />
        <TwoFactor enabled={me.twoFactorEnabled} />
        {me.source === 'local' ? <Password /> : (
          <section className="card">
            <h2>Password</h2>
            <p className="muted">Your password is managed by the company directory. Change it there.</p>
          </section>
        )}
        <p className="muted">Signed in since {when(me.signedInAt)}.</p>
      </div>
    </>
  )
}

function ApiKeys() {
  const keys = useQuery({ queryKey: ['account', 'keys'], queryFn: () => api<Keys>('/api/account/keys') })
  const queryClient = useQueryClient()
  const rotate = useMutation({
    mutationFn: () => api<{ apiKey: string }>('/api/account/keys/rotate', { body: {} }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['account', 'keys'] }),
  })
  return (
    <section className="card">
      <h2>API key</h2>
      <p className="muted">
        For your tools (Qwen Code, scripts, IDEs). Spent so far: {money(keys.data?.spend)} of {money(keys.data?.budget)}.
      </p>
      {keys.data && (
        <ul className="plain">
          {keys.data.keys.length === 0 && <li>No key yet.</li>}
          {keys.data.keys.map((k) => (
            <li key={k.alias + k.preview}>
              <code>{k.preview ?? k.alias}</code> {k.blocked && <span className="badge warn">blocked</span>}{' '}
              <span className="muted">created {when(k.createdAt)}</span>
            </li>
          ))}
        </ul>
      )}
      <ErrorText error={keys.error ?? rotate.error} />
      {rotate.data ? (
        <OnceNotice>
          <Secret label="New API key" value={rotate.data.apiKey} />
          <p className="muted">The old key has stopped working.</p>
        </OnceNotice>
      ) : (
        <button
          className="button secondary"
          disabled={rotate.isPending}
          onClick={() => {
            if (window.confirm('Make a new key? The current one stops working at once.')) rotate.mutate()
          }}
        >
          New key
        </button>
      )}
    </section>
  )
}

function TwoFactor({ enabled }: { enabled: boolean }) {
  const queryClient = useQueryClient()
  const [setup, setSetup] = useState<{ sharedKey: string; uri: string } | null>(null)
  const [qr, setQr] = useState<string | null>(null)
  const [codes, setCodes] = useState<string[] | null>(null)
  const refresh = () => queryClient.invalidateQueries({ queryKey: ['me'] })

  const begin = useMutation({
    mutationFn: () => api<{ sharedKey: string; uri: string }>('/api/account/2fa/setup', { body: {} }),
    onSuccess: setSetup,
  })
  const enable = useMutation({
    mutationFn: (code: string) => api<{ recoveryCodes: string[] }>('/api/account/2fa/enable', { body: { code } }),
    onSuccess: (r) => {
      setCodes(r.recoveryCodes)
      setSetup(null)
      refresh()
    },
  })
  const disable = useMutation({
    mutationFn: (code: string) => api('/api/account/2fa/disable', { body: { code } }),
    onSuccess: refresh,
  })

  useEffect(() => {
    if (setup) QRCode.toDataURL(setup.uri, { margin: 1, width: 200 }).then(setQr, () => setQr(null))
  }, [setup])

  const submit = (fn: (code: string) => void) => (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault()
    fn(String(new FormData(e.currentTarget).get('code') ?? ''))
  }

  return (
    <section className="card">
      <h2>Two-factor sign-in</h2>
      {codes && (
        <OnceNotice>
          <p>Recovery codes: each works once, if you lose your phone. Keep them somewhere safe.</p>
          <ul className="codes">
            {codes.map((c) => (
              <li key={c}>
                <code>{c}</code>
              </li>
            ))}
          </ul>
        </OnceNotice>
      )}
      {enabled && !codes && (
        <form onSubmit={submit((c) => disable.mutate(c))} className="inline-form">
          <p className="muted">On. To turn it off, confirm with a current code.</p>
          <label>
            Code
            <input name="code" autoComplete="one-time-code" required />
          </label>
          <button className="button secondary" disabled={disable.isPending}>
            Turn off
          </button>
        </form>
      )}
      {!enabled && !setup && (
        <>
          <p className="muted">Off. Add a code from your phone to every sign-in.</p>
          <button className="button secondary" onClick={() => begin.mutate()} disabled={begin.isPending}>
            Set up
          </button>
        </>
      )}
      {setup && (
        <form onSubmit={submit((c) => enable.mutate(c))} className="stack">
          <p>Scan this with an authenticator app (Google Authenticator, Aegis, 1Password…), then enter the code it shows.</p>
          {qr && <img src={qr} alt="QR code for your authenticator app" width={200} height={200} />}
          <p className="muted">
            Or enter this key by hand: <code>{setup.sharedKey}</code>
          </p>
          <label>
            Code
            <input name="code" autoComplete="one-time-code" inputMode="numeric" required autoFocus />
          </label>
          <button className="button" disabled={enable.isPending}>
            Turn on
          </button>
        </form>
      )}
      <ErrorText error={begin.error ?? enable.error ?? disable.error} />
    </section>
  )
}

function Password() {
  const change = useMutation({
    mutationFn: (b: { current: string; next: string }) => api('/api/account/password', { body: b }),
  })
  const [mismatch, setMismatch] = useState(false)
  return (
    <section className="card">
      <h2>Password</h2>
      <form
        className="stack"
        onSubmit={(e) => {
          e.preventDefault()
          const f = new FormData(e.currentTarget)
          const next = String(f.get('next'))
          setMismatch(next !== f.get('again'))
          if (next === f.get('again')) {
            change.mutate({ current: String(f.get('current')), next }, { onSuccess: () => (e.target as HTMLFormElement).reset() })
          }
        }}
      >
        <label>
          Current password
          <input name="current" type="password" autoComplete="current-password" required />
        </label>
        <label>
          New password
          <input name="next" type="password" autoComplete="new-password" required minLength={10} />
        </label>
        <label>
          New password again
          <input name="again" type="password" autoComplete="new-password" required />
        </label>
        <p className="muted">A few unrelated words make a strong password. Other devices are signed out.</p>
        {mismatch && (
          <p className="error" role="alert">
            The new passwords differ.
          </p>
        )}
        <ErrorText error={change.error} />
        {change.isSuccess && <p role="status">Password changed.</p>}
        <button className="button" disabled={change.isPending}>
          Change password
        </button>
      </form>
    </section>
  )
}
