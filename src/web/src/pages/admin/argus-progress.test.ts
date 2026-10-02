import { describe, expect, it } from 'vitest'
import { passPercent, repoProgress, type IndexProgress } from './argus-progress'

const p = (over: Partial<IndexProgress>): IndexProgress => ({ repos: 4, position: 1, stage: 'branch', outcomes: {}, ...over })

describe("an index pass's progress", () => {
  it('counts the repositories done and the files of the one on now', () => {
    expect(passPercent(null)).toBe(0)
    expect(passPercent(p({ position: 1, stage: 'branch' }))).toBe(0)
    expect(passPercent(p({ position: 3, stage: 'files', done: 50, total: 100 }))).toBe(63)
    expect(passPercent(p({ position: 4, stage: 'finishing' }))).toBe(99)
  })

  it('says for each repository whether it is being indexed, queued or done', () => {
    const now = p({ repo: 'g/a', branch: 'main', stage: 'files', done: 30, total: 120, outcomes: { 'g/b@main': 'ok' } })
    expect(repoProgress('g/a', true, now, [])).toEqual({ state: 'indexing', branch: 'main', percent: 25 })
    expect(repoProgress('g/b', true, now, [])).toEqual({ state: 'done' })
    expect(repoProgress('g/c', true, now, ['g/c'])).toEqual({ state: 'queued' })
    expect(repoProgress('g/d', true, now, [])).toBeNull()
    expect(repoProgress('g/a', false, now, [])).toBeNull()
  })
})
