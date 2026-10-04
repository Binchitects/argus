import { describe, expect, it } from 'vitest'
import { base64UrlToBytes, isInstalled, pushSupport } from './pwa'

describe('the installable app', () => {
  it('reads a VAPID key (base64url, no padding) as bytes', () => {
    expect([...base64UrlToBytes('AQID_-8')]).toEqual([1, 2, 3, 255, 239])
    const key = base64UrlToBytes('BP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8')
    expect(key.length).toBe(65)
    expect(key[0]).toBe(4)
  })

  it('says when the browser cannot get pushes, and when the page is not the installed app', () => {
    // jsdom has no service worker and no Push API.
    expect(pushSupport()).toBe('unsupported')
    expect(isInstalled()).toBe(false)
  })
})
