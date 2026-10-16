import { afterEach, describe, expect, it, vi } from 'vitest'
import { batched } from './batch'

afterEach(() => vi.useRealTimers())

describe('events gathered for one drawing', () => {
  it('hands on the events of a frame together, once', () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'requestAnimationFrame', 'cancelAnimationFrame'] })
    const taken: number[][] = []
    const b = batched<number>((items) => taken.push(items))
    for (let i = 1; i <= 200; i++) b.push(i)
    expect(taken).toEqual([])
    vi.advanceTimersToNextFrame()
    expect(taken).toHaveLength(1)
    expect(taken[0]).toHaveLength(200)
    // Nothing more waits: the timer set beside the frame hands on nothing.
    vi.advanceTimersByTime(200)
    expect(taken).toHaveLength(1)
    b.push(201)
    vi.advanceTimersToNextFrame()
    expect(taken).toEqual([taken[0], [201]])
  })

  it('hands on after a while when no frame is drawn (a tab in the background)', () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] })
    vi.stubGlobal('requestAnimationFrame', () => 1)
    vi.stubGlobal('cancelAnimationFrame', () => undefined)
    const taken: string[][] = []
    const b = batched<string>((items) => taken.push(items), 80)
    b.push('a')
    b.push('b')
    vi.advanceTimersByTime(79)
    expect(taken).toEqual([])
    vi.advanceTimersByTime(1)
    expect(taken).toEqual([['a', 'b']])
    vi.unstubAllGlobals()
  })

  it('hands on what waits at once when flushed (the stream ended), and nothing twice', () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'requestAnimationFrame', 'cancelAnimationFrame'] })
    const taken: number[][] = []
    const b = batched<number>((items) => taken.push(items))
    b.push(1)
    b.push(2)
    b.flush()
    expect(taken).toEqual([[1, 2]])
    b.flush()
    vi.advanceTimersToNextFrame()
    vi.advanceTimersByTime(200)
    expect(taken).toEqual([[1, 2]])
  })
})
