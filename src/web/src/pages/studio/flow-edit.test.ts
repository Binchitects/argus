import { describe, expect, it } from 'vitest'
import { allSteps, insertStep, newStep, removeStep, stepAt, updateStep, valuesBefore } from './flow-edit'
import type { FlowStep } from './studio-api'

const flow: FlowStep[] = [
  { id: 'a', kind: 'ask', prompt: 'x' },
  {
    id: 'b', kind: 'condition', op: 'contains', left: '', right: '',
    then: [{ id: 'c', kind: 'loop', over: '{{a.text}}', steps: [{ id: 'd', kind: 'text', text: '{{item}}' }] }],
    else: [],
  },
]

describe('editing a flow', () => {
  it('finds, changes, adds and takes out steps deep inside', () => {
    expect(stepAt(flow, [1, 'then', 0, 'steps', 0])?.id).toBe('d')
    const changed = updateStep(flow, [1, 'then', 0, 'steps', 0], (s) => ({ ...s, text: 'changed' }))
    expect(stepAt(changed, [1, 'then', 0, 'steps', 0])?.text).toBe('changed')
    // The rest is not touched.
    expect(stepAt(flow, [1, 'then', 0, 'steps', 0])?.text).toBe('{{item}}')
    const added = insertStep(flow, { parent: [1], branch: 'else' }, 0, { id: 'e', kind: 'email', text: 'hi' })
    expect(stepAt(added, [1, 'else', 0])?.id).toBe('e')
    const gone = removeStep(added, [1, 'then', 0])
    expect(allSteps(gone).map((s) => s.step.id)).toEqual(['a', 'b', 'e'])
  })

  it('names a new step with the next free id, and says what values a step may use', () => {
    expect(newStep('ask', flow).id).toBe('s1')
    expect(newStep('ask', [...flow, { id: 's1', kind: 'text', text: '' }]).id).toBe('s2')
    const values = valuesBefore(flow, [1, 'then', 0, 'steps', 0], ['topic']).map((v) => v.value)
    expect(values).toEqual(['{{input.text}}', '{{input.by}}', '{{input.topic}}', '{{a.text}}', '{{item}}', '{{index}}'])
  })
})
