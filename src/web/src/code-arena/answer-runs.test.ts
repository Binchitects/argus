import { describe, expect, it } from 'vitest'
import { runsOf, subAgentsSummary, toolsSummary } from './answer'

const call = (id: string, name: string) => ({ id, type: 'function' as const, function: { name, arguments: '{}' } })

describe("an answer's tool calls", () => {
  it('come in runs: sub-agents started together, and the other calls between them', () => {
    const runs = runsOf([call('1', 'read_file'), call('2', 'grep'), call('3', 'task'), call('4', 'task'), call('5', 'run_shell')])
    expect(runs.map((r) => [r.kind, r.calls.map((c) => c.id)])).toEqual([
      ['tools', ['1', '2']],
      ['task', ['3', '4']],
      ['tools', ['5']],
    ])
  })

  it('a run says what it did in one line', () => {
    expect(toolsSummary(['read_file', 'grep', 'read_file', 'run_shell', 'read_file'])).toBe('Used 5 tools · read_file ×3, grep, run_shell')
    expect(subAgentsSummary([true, false, false], 3)).toBe('3 sub-agents · 2 working')
    expect(subAgentsSummary([true, true], 2)).toBe('2 sub-agents · done')
  })
})
