import { describe, expect, it } from 'vitest'
import { reduce, stopped, withQuestion, type LiveState } from './live'
import type { ChatEvent } from './types'

const start: LiveState = { messages: [], leaf: null, notices: [], title: null, thinkingSince: null }

function play(events: ChatEvent[]) {
  let s = withQuestion(start, 'local-1', null, 'hello', [])
  for (const e of events) s = reduce(s, e, 'local-1', 1000)
  return s
}

describe('live answer', () => {
  it('builds the answer from the stream, on the right parents', () => {
    const s = play([
      { type: 'question', id: 'q1', parentId: null },
      { type: 'assistant', id: 'a1', parentId: 'q1', model: 'M' },
      { type: 'reasoning', text: 'hmm' },
      { type: 'thought', ms: 1200 },
      { type: 'content', text: 'Hi ' },
      { type: 'content', text: 'there' },
      { type: 'usage', prompt: 10, cached: 4, completion: 2, thinkingMs: 1200, durationMs: 3000 },
      { type: 'done', id: 'a1' },
    ])
    expect(s.messages.map((m) => [m.id, m.parentId])).toEqual([['q1', null], ['a1', 'q1']])
    const a = s.messages[1]!
    expect(a).toMatchObject({ content: 'Hi there', reasoning: 'hmm', thinkingMs: 1200, durationMs: 3000, model: 'M', cachedTokens: 4 })
    expect(s.leaf).toBe('a1')
  })

  it('a long tool call keeps when it started and the last progress its server reported', () => {
    const s = play([
      { type: 'question', id: 'q1', parentId: null },
      { type: 'assistant', id: 'a1', parentId: 'q1', model: 'M' },
      { type: 'tool_call', id: 'c1', name: 'build', arguments: '{}' },
      { type: 'tool_progress', id: 'c1', progress: 1, total: 4, message: 'Cloning' },
      { type: 'tool_progress', id: 'c1', progress: 2, total: 4, message: null },
    ])
    expect(s.calls?.c1).toEqual({ since: 1000, progress: 2, total: 4, message: 'Cloning' })
  })

  it("sub-agents' work builds up step by step under their delegate call", () => {
    const s = play([
      { type: 'question', id: 'q1', parentId: null },
      { type: 'assistant', id: 'a1', parentId: 'q1', model: 'M' },
      { type: 'tool_call', id: 'd1', name: 'delegate', arguments: '{}' },
      { type: 'agent', id: 'd1', index: 1, event: 'start', title: 'B', instructions: 'Do B' },
      { type: 'agent', id: 'd1', index: 0, event: 'start', title: 'A', instructions: 'Do A' },
      { type: 'agent', id: 'd1', index: 0, event: 'reasoning', text: 'Hm' },
      { type: 'agent', id: 'd1', index: 0, event: 'reasoning', text: 'm.' },
      { type: 'agent', id: 'd1', index: 0, event: 'content', text: 'Let me look.' },
      { type: 'agent', id: 'd1', index: 0, event: 'tool_call', call: { id: 'c1', name: 'calculate', arguments: '{"expression":"2+2"}' } },
      { type: 'agent', id: 'd1', index: 0, event: 'tool_result', call: { id: 'c1' }, text: '4', isError: false },
      { type: 'agent', id: 'd1', index: 0, event: 'content', text: 'It is 4.' },
      { type: 'agent', id: 'd1', index: 0, event: 'done', error: null, ms: 900 },
    ])
    const [a, b] = s.agents!.d1!
    expect(a).toMatchObject({ title: 'A', reasoning: 'Hmm.', text: 'It is 4.', status: 'done', ms: 900 })
    expect(a!.steps).toEqual([{ id: 'c1', name: 'calculate', arguments: '{"expression":"2+2"}', result: '4', isError: false }])
    expect(b).toMatchObject({ title: 'B', status: 'running', steps: [] })
  })

  it('tool results hang off the call, and the next answer off the result', () => {
    const s = play([
      { type: 'question', id: 'q1', parentId: null },
      { type: 'assistant', id: 'a1', parentId: 'q1', model: 'M' },
      { type: 'tool_call', id: 'c1', name: 'find_symbol', arguments: '{"name":"X"}' },
      { type: 'tool_result', id: 'c1', messageId: 't1', name: 'find_symbol', text: 'found', isError: false, noAccess: false, durationMs: 40 },
      { type: 'assistant', id: 'a2', parentId: 't1', model: 'M' },
      { type: 'content', text: 'It is in x.c' },
    ])
    expect(s.messages.map((m) => [m.id, m.parentId])).toEqual([['q1', null], ['a1', 'q1'], ['t1', 'a1'], ['a2', 't1']])
    expect(s.messages[1]!.toolCalls![0]!.function.name).toBe('find_symbol')
    expect(s.messages[2]!.durationMs).toBe(40)
  })

  it('times thinking from the first reasoning token until it ends', () => {
    let s = play([{ type: 'question', id: 'q1', parentId: null }, { type: 'assistant', id: 'a1', parentId: 'q1', model: 'M' }])
    s = reduce(s, { type: 'reasoning', text: 'a' }, 'local-1', 5000)
    s = reduce(s, { type: 'reasoning', text: 'b' }, 'local-1', 6000)
    expect(s.thinkingSince).toBe(5000)
    s = reduce(s, { type: 'thought', ms: 1000 }, 'local-1', 6000)
    expect(s.thinkingSince).toBeNull()
  })

  it('an error and a stop are kept on the answer', () => {
    const failed = play([{ type: 'question', id: 'q1', parentId: null }, { type: 'assistant', id: 'a1', parentId: 'q1', model: 'M' }, { type: 'error', message: 'No credit.' }])
    expect(failed.messages[1]).toMatchObject({ status: 'failed', error: 'No credit.' })
    const s = stopped(play([{ type: 'question', id: 'q1', parentId: null }, { type: 'assistant', id: 'a1', parentId: 'q1', model: 'M' }, { type: 'content', text: 'part' }]))
    expect(s.messages[1]).toMatchObject({ status: 'stopped', content: 'part' })
  })

  it('an answer watched again from its start rebuilds over what was saved, without doubles', () => {
    const events: ChatEvent[] = [
      { type: 'question', id: 'q1', parentId: null },
      { type: 'assistant', id: 'a1', parentId: 'q1', model: 'M' },
      { type: 'content', text: 'Looking.' },
      { type: 'tool_call', id: 'c1', name: 'find_symbol', arguments: '{}' },
      { type: 'tool_result', id: 'c1', messageId: 't1', name: 'find_symbol', text: 'one', isError: false, noAccess: false, durationMs: 1 },
      { type: 'assistant', id: 'a2', parentId: 't1', model: 'M' },
      { type: 'tool_call', id: 'c2', name: 'find_symbol', arguments: '{}' },
      { type: 'tool_result', id: 'c2', messageId: 't2', name: 'find_symbol', text: 'two', isError: false, noAccess: false, durationMs: 1 },
      { type: 'assistant', id: 'a3', parentId: 't2', model: 'M' },
      { type: 'content', text: 'It is in x.c' },
    ]
    const live = play(events)
    // The saved chat already has the two finished rounds; the third is being written.
    const saved = live.messages.slice(0, 5).map((m) => ({ ...m, content: m.id === 'a1' ? 'Looking.' : m.content }))
    let s: LiveState = { ...start, messages: saved, leaf: 't2' }
    for (const e of events) s = reduce(s, e, null, 1000)
    expect(s.messages.map((m) => m.id)).toEqual(['q1', 'a1', 't1', 'a2', 't2', 'a3'])
    expect(s.messages.find((m) => m.id === 'a1')).toMatchObject({ content: 'Looking.', toolCalls: [{ id: 'c1' }] })
    expect(s.messages.find((m) => m.id === 'a2')!.content).toBe('')
    expect(s.messages.find((m) => m.id === 'a3')!.content).toBe('It is in x.c')
    expect(s.leaf).toBe('a3')
  })

  it('a stop from anywhere is kept on the answer', () => {
    const s = play([{ type: 'question', id: 'q1', parentId: null }, { type: 'assistant', id: 'a1', parentId: 'q1', model: 'M' }, { type: 'content', text: 'half' }, { type: 'stopped', id: 'a1' }])
    expect(s.messages[1]).toMatchObject({ status: 'stopped', content: 'half' })
  })

  it('a compaction keeps its summary on the message it ends at, and its failure never lands on the last answer', () => {
    const saved = play([{ type: 'question', id: 'q1', parentId: null }, { type: 'assistant', id: 'a1', parentId: 'q1', model: 'M' }, { type: 'content', text: 'old answer' }])
    let s: LiveState = { ...saved, mode: 'compact' }
    s = reduce(s, { type: 'compacting' }, null)
    expect(s.compacting).toBe(true)
    s = reduce(s, { type: 'compacted', id: 'a1', summary: 'In short.', auto: false, covered: 2 }, null)
    expect(s).toMatchObject({ compacting: false, mode: 'compact' })
    expect(s.messages[1]!.summary).toBe('In short.')
    const failed = reduce({ ...saved, mode: 'compact' }, { type: 'error', message: 'No model.' }, null)
    expect(failed.messages[1]).toMatchObject({ status: 'complete', error: null })
  })

  it('keeps notices and the new title', () => {
    const s = play([{ type: 'title', title: 'Hello' }, { type: 'notice', kind: 'no_vision', text: 'Cannot see.' }])
    expect(s.title).toBe('Hello')
    expect(s.notices).toEqual([{ kind: 'no_vision', text: 'Cannot see.' }])
  })
})
