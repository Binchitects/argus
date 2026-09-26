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

  it('keeps notices and the new title', () => {
    const s = play([{ type: 'title', title: 'Hello' }, { type: 'notice', kind: 'no_vision', text: 'Cannot see.' }])
    expect(s.title).toBe('Hello')
    expect(s.notices).toEqual([{ kind: 'no_vision', text: 'Cannot see.' }])
  })
})
