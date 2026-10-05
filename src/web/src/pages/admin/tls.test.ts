import { describe, expect, it } from 'vitest'
import { certificateText, tlsBody } from './tls'

describe('a CA certificate from a file', () => {
  it('keeps PEM as it is and wraps DER in PEM', async () => {
    const pem = '-----BEGIN CERTIFICATE-----\nMIIBcorp\n-----END CERTIFICATE-----'
    expect(await certificateText(new File([`${pem}\n\n`], 'ca.pem'))).toBe(pem)
    const der = new Uint8Array(100).map((_, i) => (i * 37) % 256)
    const wrapped = await certificateText(new File([der], 'ca.cer'))
    const lines = wrapped.split('\n')
    expect(lines[0]).toBe('-----BEGIN CERTIFICATE-----')
    expect(lines.at(-1)).toBe('-----END CERTIFICATE-----')
    expect(lines[1]).toHaveLength(64)
    expect(Uint8Array.from(atob(lines.slice(1, -1).join('')), (c) => c.charCodeAt(0))).toEqual(der)
  })

  it('is sent only when the CA is trusted', () => {
    expect(tlsBody({ tls: 'OwnCa', tlsCa: 'PEM' })).toEqual({ tls: 'OwnCa', tlsCa: 'PEM' })
    expect(tlsBody({ tls: 'Off', tlsCa: 'PEM' })).toEqual({ tls: 'Off', tlsCa: null })
  })
})
