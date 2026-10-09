import { describe, expect, it } from 'vitest'
import { answerUsage } from './format'
import { blank } from './live'
import type { ChatConfig } from './types'

const config = {
  models: [
    { name: 'Main', prices: { input: 0.2, cachedInput: 0.02, output: 0.8 } },
    { name: 'Free', prices: { input: null, cachedInput: null, output: null } },
  ],
} as unknown as ChatConfig

describe('what an answer used and cost', () => {
  it("counts its sub-agents' model calls with its own, at each one's model's prices", () => {
    const a1 = { ...blank('a1', 'assistant', 'q'), model: 'Main', promptTokens: 1000, cachedTokens: 400, completionTokens: 200 }
    const agent = (prompt: number, cached: number, completion: number) => ({
      title: 't', instructions: '', reasoning: '', text: '', steps: [], status: 'done' as const, error: null, ms: 1, model: 'Main', usage: { prompt, cached, completion },
    })
    const t1 = { ...blank('t1', 'tool', 'a1'), details: { agents: [agent(5000, 4000, 300), agent(2000, 0, 100)] } }
    const a2 = { ...blank('a2', 'assistant', 't1'), model: 'Main', promptTokens: 3000, cachedTokens: 1000, completionTokens: 50 }
    const u = answerUsage([a1, t1, a2], config)
    expect(u).toMatchObject({ prompt: 11000, cached: 5400, completion: 650, agents: { prompt: 7000, cached: 4000, completion: 400 } })
    // (600*.2 + 400*.02 + 200*.8) + (1000*.2 + 4000*.02 + 300*.8) + (2000*.2 + 100*.8) + (2000*.2 + 1000*.02 + 50*.8), per million
    expect(u.cost).toBeCloseTo((288 + 520 + 480 + 460) / 1e6, 12)
    // Worked out at today's prices: these parts ran before costs were kept.
    expect(u.estimated).toBe(true)
  })

  it('adds up the cost each part kept when it ran: its rounds, a picture, its sub-agents (prices since changed or not)', () => {
    const agent = { title: 't', instructions: '', reasoning: '', text: '', steps: [], status: 'done' as const, error: null, ms: 1, model: 'Main', usage: { prompt: 500, cached: 100, completion: 20 } }
    const a1 = { ...blank('a1', 'assistant', 'q'), model: 'Main', promptTokens: 1000, cachedTokens: 400, completionTokens: 200, cost: 0.001 }
    const picture = { ...blank('t1', 'tool', 'a1'), cost: 0.04 }
    const agents = { ...blank('t2', 'tool', 't1'), details: { agents: [agent] }, cost: 0.0005 }
    const a2 = { ...blank('a2', 'assistant', 't2'), model: 'Free', promptTokens: 100, cachedTokens: 0, completionTokens: 10, cost: 0.0002 }
    const u = answerUsage([a1, picture, agents, a2], config)
    expect(u).toMatchObject({ prompt: 1600, cached: 500, completion: 230, agents: { prompt: 500, cached: 100, completion: 20 }, estimated: false })
    // Kept costs as they are, even for a model with no price now.
    expect(u.cost).toBeCloseTo(0.001 + 0.04 + 0.0005 + 0.0002, 12)
  })

  it('has no cost when no model has a price', () => {
    const a = { ...blank('a', 'assistant', 'q'), model: 'Free', promptTokens: 10, cachedTokens: 0, completionTokens: 5 }
    expect(answerUsage([a], config)).toMatchObject({ prompt: 10, completion: 5, cost: null })
  })
})
