import { describe, expect, it } from 'vitest'
import { seconds } from './format'
import { ChatTree, toTurns } from './tree'
import type { Message } from './types'

const m = (id: string, parentId: string | null, role: Message['role'], content = id): Message => ({
  id, parentId, role, content, reasoning: null, toolName: null, toolCallId: null, toolCalls: null, attachments: [], status: 'complete',
  error: null, model: null, promptTokens: null, cachedTokens: null, completionTokens: null, thinkingMs: null, durationMs: null, createdAt: '', noAccess: false,
})

// q1 -> a1 -> q2 -> a2
//          \-> q2' -> a2'      (an edited second question)
//     \-> a1b                  (the first question answered again)
const messages = [m('q1', null, 'user'), m('a1', 'q1', 'assistant'), m('q2', 'a1', 'user'), m('a2', 'q2', 'assistant'), m('q2e', 'a1', 'user'), m('a2e', 'q2e', 'assistant'), m('a1b', 'q1', 'assistant')]

describe('chat tree', () => {
  const tree = new ChatTree(messages)

  it('the path to a leaf follows parents', () => {
    expect(tree.path('a2').map((x) => x.id)).toEqual(['q1', 'a1', 'q2', 'a2'])
    expect(tree.path('a2e').map((x) => x.id)).toEqual(['q1', 'a1', 'q2e', 'a2e'])
  })

  it('siblings are the alternatives, oldest first', () => {
    expect(tree.siblings(tree.byId.get('q2e')!).map((x) => x.id)).toEqual(['q2', 'q2e'])
    expect(tree.siblings(tree.byId.get('a1')!).map((x) => x.id)).toEqual(['a1', 'a1b'])
  })

  it('switching to a message shows its newest line', () => {
    expect(tree.leafBelow('a1')).toBe('a2e')
    expect(tree.leafBelow('q2')).toBe('a2')
    expect(tree.leafBelow('a1b')).toBe('a1b')
  })

  it('a path becomes questions with their answers, tools included', () => {
    const withTools = new ChatTree([m('q', null, 'user'), m('a', 'q', 'assistant'), m('t', 'a', 'tool'), m('b', 't', 'assistant')])
    const turns = toTurns(withTools.path('b'))
    expect(turns).toHaveLength(1)
    expect(turns[0]!.question!.id).toBe('q')
    expect(turns[0]!.answer.map((x) => x.id)).toEqual(['a', 't', 'b'])
  })

  it('a loop in the data cannot hang the page', () => {
    const loop = new ChatTree([m('x', 'y', 'user'), m('y', 'x', 'assistant')])
    expect(loop.path('x').length).toBeLessThanOrEqual(2)
  })
})


describe('durations', () => {
  it('read naturally', () => {
    expect(seconds(420)).toBe('0.4 s')
    expect(seconds(2300)).toBe('2.3 s')
    expect(seconds(42_000)).toBe('42 s')
    expect(seconds(65_000)).toBe('1 min 5 s')
  })
})
