import { describe, expect, it } from 'vitest'
import { blank } from './live'
import { partsDone, researchStep } from './research'
import type { AgentWork, Message } from './types'

const parts = JSON.stringify({ tasks: [1, 2, 3, 4].map((i) => ({ title: `Part ${i}`, instructions: 'Look it up.' })) })
const asking = (calls: { id: string; name: string; args?: string }[]): Message => ({
  ...blank('a1', 'assistant', 'q1', 'Plan: four questions.'),
  toolCalls: calls.map((c) => ({ id: c.id, function: { name: c.name, arguments: c.args ?? '{}' } })),
})
const result = (callId: string, agents?: AgentWork[]): Message => ({ ...blank(`r-${callId}`, 'tool', 'a1', '[]'), toolCallId: callId, toolName: 'x', details: agents ? { agents } : null })
const agent = (status: AgentWork['status']): AgentWork => ({ title: '', instructions: '', reasoning: '', text: '', steps: [], status, error: null, ms: null })

describe('deep research in words', () => {
  it('plans, researches its parts with how many are done, fills gaps, then writes the report', () => {
    expect(researchStep([{ ...blank('a1', 'assistant', 'q1'), reasoning: 'Thinking.' }], undefined)).toBe('Planning the research')

    const delegating = asking([{ id: 'd1', name: 'delegate', args: parts }])
    expect(researchStep([delegating], {})).toBe('Researching 4 parts: 0 of 4 parts done')
    expect(researchStep([delegating], { d1: [agent('done'), agent('done'), agent('running'), agent('failed')] })).toBe('Researching 4 parts: 2 of 4 parts done')
    const researched = result('d1', [agent('done'), agent('done'), agent('done'), agent('done')])

    // The gaps go to sub-agents again: a second delegation.
    const twoGaps = JSON.stringify({ tasks: [1, 2].map((i) => ({ title: `Gap ${i}`, instructions: 'Look it up.' })) })
    const delegatingGaps = { ...asking([{ id: 'd2', name: 'delegate', args: twoGaps }]), id: 'a2' }
    expect(researchStep([delegating, researched, delegatingGaps], { d2: [agent('done'), agent('running')] })).toBe('Filling gaps: 1 of 2 parts done')
    expect(researchStep([delegating, researched, delegatingGaps, result('d2', [agent('done'), agent('done')]), blank('a3', 'assistant', 'r-d2', '# Report')], undefined)).toBe(
      'Writing the report',
    )

    // Or the answer reads the gaps itself (the web asks before each call, so the parts had none).
    const gaps = { ...asking([{ id: 'w1', name: 'web_search' }]), id: 'a2' }
    expect(researchStep([delegating, researched, gaps], undefined)).toBe('Filling gaps: Web search')
    expect(researchStep([delegating, researched, gaps, result('w1'), blank('a3', 'assistant', 'r-w1', '# Report')], undefined)).toBe('Writing the report')
  })

  it('a delegation refused for its arguments is not research yet: the next one is', () => {
    const wrong = asking([{ id: 'd1', name: 'delegate', args: JSON.stringify({ tasks: [{ title: 'Only', instructions: 'x' }] }) }])
    const again = { ...asking([{ id: 'd2', name: 'delegate', args: parts }]), id: 'a2' }
    expect(researchStep([wrong, result('d1'), again], {})).toBe('Researching 4 parts: 0 of 4 parts done')
  })

  it('delegate calls of one round are one step: the second is still research, not gaps', () => {
    const two = JSON.stringify({ tasks: [1, 2].map((i) => ({ title: `Part ${i}`, instructions: 'Look it up.' })) })
    const both = asking([
      { id: 'd1', name: 'delegate', args: two },
      { id: 'd2', name: 'delegate', args: two },
    ])
    const first = result('d1', [agent('done'), agent('done')])
    expect(researchStep([both, first], { d2: [agent('running')] })).toBe('Researching 2 parts: 0 of 2 parts done')
    // The next round's delegation fills the gaps.
    const gaps = { ...asking([{ id: 'd3', name: 'delegate', args: two }]), id: 'a2' }
    expect(researchStep([both, first, result('d2', [agent('done'), agent('done')]), gaps], {})).toBe('Filling gaps: 0 of 2 parts done')
  })

  it('started by the model, the research is what comes after its call', () => {
    const started = asking([{ id: 's1', name: 'deep_research', args: JSON.stringify({ question: 'Codecs' }) }])
    const on = result('s1')
    expect(researchStep([started, on], undefined)).toBe('Planning the research')
    const delegating = { ...asking([{ id: 'd1', name: 'delegate', args: parts }]), id: 'a2' }
    expect(researchStep([started, on, delegating], { d1: [agent('done')] })).toBe('Researching 4 parts: 1 of 4 parts done')
    expect(researchStep([started, on, delegating, result('d1', [agent('done')]), blank('a3', 'assistant', 'r-d1', '# Report')], undefined)).toBe('Writing the report')
  })

  it('without sub-agents, researches with the tools it has', () => {
    expect(researchStep([asking([{ id: 'w1', name: 'web_search' }])], undefined)).toBe('Researching: Web search')
  })

  it('says parts in words', () => {
    expect(partsDone(2, 4)).toBe('2 of 4 parts done')
    expect(partsDone(0, 1)).toBe('0 of 1 part done')
  })
})
