/** How a tool server's https certificate is checked: by the system's CAs, by a CA of the admin's, or not at all. */
export type TlsCheck = 'System' | 'OwnCa' | 'Off'

export interface Tls {
  tls: TlsCheck
  /** The CA to trust, in PEM (with OwnCa). */
  tlsCa: string
}

/** A server's certificate as the test saw it refused: whose it is, who issued it, and why. */
export interface CertificateProblem {
  subject: string
  issuer: string
  names: string[]
  notBefore: string
  notAfter: string
  selfSigned: boolean
  sha256: string
  reasons: string[]
}

/** What a request sends: the CA only with "trust this CA". */
export const tlsBody = (t: Tls) => ({ tls: t.tls, tlsCa: t.tls === 'OwnCa' ? t.tlsCa : null })

/** A certificate file as PEM: a PEM file as it is, a DER one (.cer, .crt) wrapped in PEM. */
export async function certificateText(file: File): Promise<string> {
  const bytes = new Uint8Array(await file.arrayBuffer())
  const text = new TextDecoder().decode(bytes)
  if (text.includes('-----BEGIN')) return text.trim()
  let binary = ''
  for (let i = 0; i < bytes.length; i += 0x8000) binary += String.fromCharCode(...bytes.subarray(i, i + 0x8000))
  return `-----BEGIN CERTIFICATE-----\n${btoa(binary).match(/.{1,64}/g)?.join('\n') ?? ''}\n-----END CERTIFICATE-----`
}
