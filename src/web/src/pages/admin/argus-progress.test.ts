import { describe, expect, it } from 'vitest'
import { isWorking, passPercent, progressPercent, progressWords, type IndexProgress } from './argus-progress'

const p = (over: Partial<IndexProgress>): IndexProgress => ({ repos: 4, position: 1, stage: 'branch', outcomes: {}, ...over })

describe("an index pass's progress", () => {
  it('counts the repositories done and the files of the one on now', () => {
    expect(passPercent(null)).toBe(0)
    expect(passPercent(p({ position: 1, stage: 'branch' }))).toBe(0)
    expect(passPercent(p({ position: 3, stage: 'files', done: 50, total: 100 }))).toBe(63)
    expect(passPercent(p({ position: 4, stage: 'finishing' }))).toBe(99)
  })

  it('says for each repository where it is, in words, and how far its step is', () => {
    expect(progressWords({ state: 'queued' })).toBe('Waiting for its turn')
    expect(progressWords({ state: 'fetching' })).toBe('Fetching from GitLab…')
    expect(progressWords({ state: 'files', branch: 'main', done: 30, total: 1200 })).toBe('Reading files on main: 30 of 1,200')
    expect(progressPercent({ state: 'files', branch: 'main', done: 30, total: 120 })).toBe(25)
    expect(progressWords({ state: 'symbols', branch: 'v2', total: 1 })).toBe('Reading symbols on v2 from 1 file…')
    expect(progressPercent({ state: 'symbols', branch: 'v2', total: 1 })).toBeNull()
    expect(progressWords({ state: 'embedding', done: 64, total: 200 })).toBe('Embedding for meaning search: 64 of 200')
    expect(progressWords({ state: 'failed', message: 'Could not fetch it from GitLab: 403.' })).toBe('Could not fetch it from GitLab: 403.')
    expect(isWorking({ state: 'symbols' })).toBe(true)
    expect(isWorking({ state: 'queued' })).toBe(false)
    expect(isWorking(null)).toBe(false)
  })
})
