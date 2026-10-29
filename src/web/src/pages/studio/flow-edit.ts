import type { FlowStep, RunStep, StepKind, TriggerKind } from './studio-api'

/** Where a step is: its index in the top lane, then in which inner lane (then, else, a loop's steps) and its index there. */
export type Loc = (number | 'then' | 'else' | 'steps')[]

/** A lane of steps: the top one, or an inner one (a condition's then or else, a loop's steps). */
export interface Lane {
  parent: Loc | null
  branch: 'then' | 'else' | 'steps' | null
}

export function laneOf(steps: FlowStep[], lane: Lane): FlowStep[] {
  if (!lane.parent || !lane.branch) return steps
  const parent = stepAt(steps, lane.parent)
  return (parent?.[lane.branch] as FlowStep[] | null | undefined) ?? []
}

export function stepAt(steps: FlowStep[], loc: Loc): FlowStep | undefined {
  let lane: FlowStep[] = steps
  let step: FlowStep | undefined
  for (let i = 0; i < loc.length; i++) {
    const part = loc[i]!
    if (typeof part === 'number') {
      step = lane[part]
      if (!step) return undefined
    } else {
      lane = (step?.[part] as FlowStep[] | null | undefined) ?? []
    }
  }
  return step
}

/** The steps with one lane changed (a copy all the way down to it). */
function withLane(steps: FlowStep[], lane: Lane, change: (l: FlowStep[]) => FlowStep[]): FlowStep[] {
  if (!lane.parent || !lane.branch) return change(steps)
  const [first, ...rest] = lane.parent
  const at = first as number
  return steps.map((s, i) => {
    if (i !== at) return s
    if (rest.length === 0) return { ...s, [lane.branch!]: change((s[lane.branch!] as FlowStep[] | null | undefined) ?? []) }
    const [branch, ...inner] = rest as ['then' | 'else' | 'steps', ...Loc]
    return { ...s, [branch]: withLane((s[branch] as FlowStep[] | null | undefined) ?? [], { parent: inner, branch: lane.branch }, change) }
  })
}

export function insertStep(steps: FlowStep[], lane: Lane, index: number, step: FlowStep): FlowStep[] {
  return withLane(steps, lane, (l) => [...l.slice(0, index), step, ...l.slice(index)])
}

export function updateStep(steps: FlowStep[], loc: Loc, change: (s: FlowStep) => FlowStep): FlowStep[] {
  const index = loc[loc.length - 1] as number
  const parent = loc.length > 1 ? loc.slice(0, -2) : null
  const branch = loc.length > 1 ? (loc[loc.length - 2] as Lane['branch']) : null
  return withLane(steps, { parent, branch }, (l) => l.map((s, i) => (i === index ? change(s) : s)))
}

export function removeStep(steps: FlowStep[], loc: Loc): FlowStep[] {
  const index = loc[loc.length - 1] as number
  const parent = loc.length > 1 ? loc.slice(0, -2) : null
  const branch = loc.length > 1 ? (loc[loc.length - 2] as Lane['branch']) : null
  return withLane(steps, { parent, branch }, (l) => l.filter((_, i) => i !== index))
}

/** Every step, inner ones too, depth first, with where it is. */
export function allSteps(steps: FlowStep[], prefix: Loc = []): { step: FlowStep; loc: Loc }[] {
  return steps.flatMap((s, i) => {
    const loc = [...prefix, i]
    return [
      { step: s, loc },
      ...allSteps(s.then ?? [], [...loc, 'then']),
      ...allSteps(s.else ?? [], [...loc, 'else']),
      ...allSteps(s.steps ?? [], [...loc, 'steps']),
    ]
  })
}

/** A new step of a kind, its id the next free one (s1, s2…), with what it needs filled in to start. */
export function newStep(kind: StepKind, steps: FlowStep[]): FlowStep {
  const taken = new Set(allSteps(steps).map((s) => s.step.id))
  let n = 1
  while (taken.has(`s${n}`)) n++
  const id = `s${n}`
  switch (kind) {
    case 'ask':
      return { id, kind, prompt: 'Answer briefly: {{input.text}}' }
    case 'agent':
      return { id, kind, prompt: 'Find out: {{input.text}}', tools: ['web'] }
    case 'tool':
      return { id, kind, tool: null, function: null, arguments: {} }
    case 'condition':
      return { id, kind, left: '', op: 'contains', right: '', then: [], else: [] }
    case 'loop':
      return { id, kind, over: '', most: 10, steps: [] }
    case 'approval':
      return { id, kind, question: 'Go on?' }
    case 'email':
      return { id, kind, subject: '', text: '' }
    case 'file':
      return { id, kind, fileName: 'result.md', text: '' }
    case 'knowledge':
      return { id, kind, prompt: '{{input.text}}' }
    case 'data':
      return { id, kind, sourceId: null, prompt: '{{input.text}}' }
    case 'http':
      return { id, kind, method: 'GET', url: 'https://', arguments: {} }
    case 'code':
      return { id, kind, text: "import json\nvalues = json.load(open('input.json'))\nprint(values['input']['text'])" }
    case 'wait':
      return { id, kind, minutes: 60 }
    case 'stop':
      return { id, kind, outcome: 'done', text: '' }
    case 'gitlab':
      return { id, kind, action: 'comment', text: '' }
    case 'notify':
      return { id, kind, subject: '', text: '' }
    default:
      return { id, kind, text: '' }
  }
}

/** The values a step may use: the run's input, its fields, the steps before it (and a loop's item, inside one). */
export function valuesBefore(steps: FlowStep[], loc: Loc, fields: string[], trigger?: TriggerKind): { label: string; value: string }[] {
  const out = [{ label: 'The input', value: '{{input.text}}' }, { label: 'Who started it', value: '{{input.by}}' }, ...fields.map((f) => ({ label: `Field ${f}`, value: `{{input.${f}}}` }))]
  if (trigger === 'gitlab') {
    for (const [label, key] of [['The event', 'event'], ['The project', 'project'], ['The branch', 'branch'], ['Its title', 'title'], ['Its link', 'url'], ['Its number', 'iid'], ['Who did it', 'author'], ['Its labels', 'labels']] as const) {
      out.push({ label, value: `{{input.gitlab.${key}}}` })
    }
  }
  let lane = steps
  for (let i = 0; i < loc.length; i++) {
    const part = loc[i]!
    if (typeof part !== 'number') continue
    for (const s of lane.slice(0, part)) {
      out.push({ label: `${s.name || s.id}'s answer`, value: `{{${s.id}.text}}` })
      if ((s.kind === 'ask' || s.kind === 'text') && s.json) out.push({ label: `${s.name || s.id}'s JSON`, value: `{{${s.id}.json}}` })
      if (s.kind === 'data') out.push({ label: `${s.name || s.id}'s first row`, value: `{{${s.id}.json.rows.0}}` }, { label: `${s.name || s.id}'s rows`, value: `{{${s.id}.json.rows}}` })
      if (s.kind === 'knowledge' || s.kind === 'http' || s.kind === 'code' || (s.kind === 'gitlab' && s.action === 'issue')) out.push({ label: `${s.name || s.id}'s JSON`, value: `{{${s.id}.json}}` })
    }
    const here = lane[part]
    const next = loc[i + 1]
    if (here && typeof next === 'string') {
      if (here.kind === 'loop' && next === 'steps') out.push({ label: 'The item', value: '{{item}}' }, { label: 'Its number (from 0)', value: '{{index}}' })
      lane = (here[next] as FlowStep[] | null | undefined) ?? []
    }
  }
  return out
}

/** A step's last run, by its id, for the canvas to show how it went. */
export function lastByStep(steps: RunStep[]): Map<string, RunStep> {
  const m = new Map<string, RunStep>()
  for (const s of steps) m.set(s.stepId, s)
  return m
}
