import { QueryClient, QueryClientProvider, useQuery } from '@tanstack/react-query'
import { render, waitFor } from '@testing-library/react'
import { describe, expect, it, onTestFinished, vi } from 'vitest'
import { useUsageLive } from './usage-live'

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

describe('use as it is booked', () => {
  it("reads a page's numbers again at each beat, a project's only when the beat names it", async () => {
    vi.stubGlobal('EventSource', FakeSource)
    onTestFinished(() => {
      vi.unstubAllGlobals()
    })
    const reads = { all: 0, project: 0 }
    function Page() {
      useUsageLive((projects) => [['all'], ...(projects.includes('p1') ? [['p1']] : [])])
      useQuery({ queryKey: ['all'], queryFn: () => ++reads.all })
      useQuery({ queryKey: ['p1'], queryFn: () => ++reads.project })
      return null
    }
    const client = new QueryClient()
    const view = render(
      <QueryClientProvider client={client}>
        <Page />
      </QueryClientProvider>,
    )
    await waitFor(() => expect(reads).toEqual({ all: 1, project: 1 }))
    expect(FakeSource.last?.url).toBe('/api/usage/live')
    FakeSource.last!.onmessage!({ data: JSON.stringify({ type: 'usage', projects: ['p2'] }) })
    await waitFor(() => expect(reads.all).toBe(2))
    expect(reads.project).toBe(1)
    FakeSource.last!.onmessage!({ data: JSON.stringify({ type: 'usage', projects: ['p1'] }) })
    await waitFor(() => expect(reads).toEqual({ all: 3, project: 2 }))
    // Not a beat: nothing read.
    FakeSource.last!.onmessage!({ data: JSON.stringify({ type: 'watching' }) })
    view.unmount()
    expect(FakeSource.last!.closed).toBe(true)
  })
})
