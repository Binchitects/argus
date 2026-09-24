import { useState } from 'react'

/** A value shown once (a password, an API key): readable, copyable, and clearly never shown again. */
export function Secret({ label, value }: { label: string; value: string }) {
  const [copied, setCopied] = useState(false)
  return (
    <div className="secret">
      <span className="secret-label">{label}</span>
      <code aria-label={label}>{value}</code>
      <button
        type="button"
        className="button small secondary"
        onClick={async () => {
          try {
            await navigator.clipboard.writeText(value)
            setCopied(true)
          } catch {
            setCopied(false)
          }
        }}
      >
        {copied ? 'Copied' : 'Copy'}
      </button>
    </div>
  )
}

export function OnceNotice({ children }: { children: React.ReactNode }) {
  return (
    <div className="notice once" role="status">
      <p>
        <strong>Shown once.</strong> Copy it now; it cannot be shown again.
      </p>
      {children}
    </div>
  )
}
