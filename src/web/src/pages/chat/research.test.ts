import { describe, expect, it } from 'vitest'
import { blank } from './live'
import { partsDone, researchStep } from './research'
import type { AgentWork, Message } from './types'

const parts = JSON.stringify({ tasks: [1, 2, 3, 4].map((i) => ({ title: `Part ${i}`, instructions: 'Look it up.' })) })
const asking = (calls: { id: string; name: string; args?: string }[]): Message => ({
  ...blank('a1', 'assistant', 'q1', 'Plan: four questions.'),
  toolCalls: calls.map((c) => ({ id: c.id, function: { name: c.name, arguments: c.args ?? '{}' } })),
})
const result = (callId: string): Message => ({ ...blank(`r-${callId}`, 'tool', 'a1', '[]'), toolCallId: callId, toolName: 'x' })
const agent = (status: AgentWork['status']): AgentWork => ({ title: '', instructions: '', reasoning: '', text: '', steps: [], status, error: null, ms: null })

describe('deep research in words', () => {
  it('plans, researches its parts with how many are done, fills gaps, then writes the report', () => {
    expect(researchStep([{ ...blank('a1', 'assistant', 'q1'), reasoning: 'Thinking.' }], undefined)).toBe('Planning the research')

    const delegating = asking([{ id: 'd1', name: 'delegate', args: parts }])
    expect(researchStep([delegating], {})).toBe('Researching 4 parts: 0 of 4 parts done')
    expect(researchStep([delegating], { d1: [agent('done'), agent('done'), agent('running'), agent('failed')] })).toBe('Researching 4 parts: 2 of 4 parts done')

    const gaps = { ...asking([{ id: 'w1', name: 'web_search' }]), id: 'a2' }
    expect(researchStep([delegating, result('d1'), gaps], undefined)).toBe('Filling gaps: Web search')
    expect(researchStep([delegating, result('d1'), gaps, result('w1'), blank('a3', 'assistant', 'r-w1', '# Report')], undefined)).toBe('Writing the report')
  })

  it('without sub-agents, researches with the tools it has', () => {
    expect(researchStep([asking([{ id: 'w1', name: 'web_search' }])], undefined)).toBe('Researching: Web search')
  })

  it('says parts in words', () => {
    expect(partsDone(2, 4)).toBe('2 of 4 parts done')
    expect(partsDone(0, 1)).toBe('0 of 1 part done')
  })
})
