import { waitFor } from '@testing-library/react'
import { describe, expect, it, onTestFinished, vi } from 'vitest'
import { fakeApi, member, renderApp } from '@/test/utils'

/** The browser's EventSource, held so the test says what the server sends. */
class FakeSource {
  static last: FakeSource | null = null
  onmessage: ((m: { data: string }) => void) | null = null
  closed = false
  url: string
  constructor(url: string) {
    this.url = url
    FakeSource.last = this
  }
  close() {
    this.closed = true
  }
}

describe('the chats change as it happens', () => {
  it("reads the chats' list again when the server says one changed (Code Arena added to it), at once", async () => {
    vi.stubGlobal('EventSource', FakeSource)
    onTestFinished(() => {
      vi.unstubAllGlobals()
    })
    const calls = fakeApi(member, { 'GET /api/chat/conversations': () => ({ json: [] }) })
    renderApp('/chat')
    await waitFor(() => expect(FakeSource.last?.url).toBe('/api/chat/watch'))
    await waitFor(() => expect(calls.some((c) => c.path.startsWith('/api/chat/conversations'))).toBe(true))
    const before = calls.filter((c) => c.path.startsWith('/api/chat/conversations')).length
    FakeSource.last!.onmessage!({ data: JSON.stringify({ type: 'chat', id: 'c9', what: 'messages' }) })
    await waitFor(() => expect(calls.filter((c) => c.path.startsWith('/api/chat/conversations')).length).toBeGreaterThan(before))
  })
})
