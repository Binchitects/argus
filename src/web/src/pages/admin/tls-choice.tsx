import { ShieldCheck, ShieldOff, Upload } from 'lucide-react'
import { useRef, type Ref } from 'react'
import { Alert } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Field } from '@/components/ui/field'
import { Textarea } from '@/components/ui/input'
import { when } from '@/lib/format'
import { certificateText, type CertificateProblem, type Tls, type TlsCheck } from './tls'

const choices: { value: TlsCheck; label: string; hint: string }[] = [
  { value: 'System', label: 'Check the certificate', hint: "Against the CAs the app's system trusts. The default." },
  { value: 'OwnCa', label: 'Trust this CA', hint: 'For a company CA, or a self-signed server (its own certificate): the chain must lead to it, and the name must still match.' },
  { value: 'Off', label: 'Do not check', hint: 'Any certificate is accepted.' },
]

/** The choice, in a server's form: three radios, the CA (pasted or from a file), and the warning when nothing is checked. */
export function TlsChoice({ name, value, onChange, caRef }: { name: string; value: Tls; onChange: (t: Tls) => void; caRef?: Ref<HTMLTextAreaElement> }) {
  const file = useRef<HTMLInputElement>(null)
  return (
    <fieldset className="grid gap-2">
      <legend className="mb-1 text-sm font-medium">Its certificate (https)</legend>
      {choices.map((c) => (
        <label key={c.value} className="flex items-start gap-2 text-sm">
          <input type="radio" name={name} className="mt-1 size-3.5 accent-primary" value={c.value} checked={value.tls === c.value} onChange={() => onChange({ ...value, tls: c.value })} />
          <span className="grid gap-0.5">
            {c.label}
            <span className="text-xs text-muted-foreground">{c.hint}</span>
          </span>
        </label>
      ))}
      {value.tls === 'OwnCa' && (
        <div className="grid gap-2 ps-5">
          <Field label="CA certificate (PEM)" hint="Its certificate only, never a key. Several (a chain) are fine.">
            <Textarea
              ref={caRef}
              required
              className="min-h-28 font-mono text-xs"
              spellCheck={false}
              placeholder={'-----BEGIN CERTIFICATE-----\n…\n-----END CERTIFICATE-----'}
              value={value.tlsCa}
              onChange={(e) => onChange({ ...value, tlsCa: e.target.value })}
            />
          </Field>
          <input
            ref={file}
            type="file"
            accept=".pem,.crt,.cer,.der,application/x-pem-file,application/x-x509-ca-cert,application/pkix-cert"
            className="hidden"
            aria-label="CA certificate file"
            onChange={async (e) => {
              const f = e.target.files?.[0]
              e.target.value = ''
              if (f) onChange({ ...value, tlsCa: await certificateText(f) })
            }}
          />
          <Button type="button" variant="outline" size="sm" className="w-fit" onClick={() => file.current?.click()}>
            <Upload /> Upload a file
          </Button>
        </div>
      )}
      {value.tls === 'Off' && (
        <Alert variant="warning" title="Its certificate will not be checked">
          The connection is encrypted, but anyone between this server and it could pose as it and read what is sent, keys and people's data included. Use it only on a network you
          trust; trusting its CA is safer.
        </Alert>
      )}
    </fieldset>
  )
}

/** The test's answer when the certificate was refused: what it is, why, and the two ways on. */
export function CertificateRefused({ problem, onTrust, onSkip }: { problem: CertificateProblem; onTrust: () => void; onSkip: () => void }) {
  return (
    <Alert
      variant="destructive"
      title="Its certificate is not trusted"
      action={
        <span className="flex flex-wrap gap-2">
          <Button type="button" variant="outline" size="sm" onClick={onTrust}>
            <ShieldCheck /> Trust its CA
          </Button>
          <Button type="button" variant="outline" size="sm" onClick={onSkip}>
            <ShieldOff /> Do not check
          </Button>
        </span>
      }
    >
      <ul className="grid gap-0.5 text-foreground">
        {problem.reasons.map((r) => (
          <li key={r}>{r}</li>
        ))}
      </ul>
      <dl className="mt-2 grid grid-cols-[auto_minmax(0,1fr)] gap-x-3 gap-y-0.5 text-xs">
        <dt>Issued to</dt>
        <dd className="font-mono break-all">{problem.subject}</dd>
        <dt>Issued by</dt>
        <dd className="font-mono break-all">{problem.selfSigned ? 'itself (self-signed)' : problem.issuer}</dd>
        <dt>For</dt>
        <dd className="break-all">{problem.names.join(', ')}</dd>
        <dt>Valid</dt>
        <dd>
          {when(problem.notBefore)} to {when(problem.notAfter)}
        </dd>
        <dt>SHA-256</dt>
        <dd className="font-mono break-all">{problem.sha256}</dd>
      </dl>
    </Alert>
  )
}

/** On a tool's card, when its certificate is not checked. */
export function TlsWarning({ name }: { name: string }) {
  return (
    <Alert variant="warning" title="Its certificate is not checked">
      Anyone between this server and {name} could pose as it; trusting its CA is safer.
    </Alert>
  )
}

/** On a tool's card, when it trusts a CA of its own. */
export function TlsCaLine({ names }: { names: string[] }) {
  return (
    <p className="flex items-center gap-1.5 text-muted-foreground">
      <ShieldCheck className="size-3.5 shrink-0" aria-hidden="true" /> Trusts its own CA: <span className="text-foreground">{names.join(', ') || 'saved'}</span>
    </p>
  )
}
