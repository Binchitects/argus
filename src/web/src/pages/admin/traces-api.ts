/** Tokens of an answer or a step, and the part of the prompt read from the engine's cache. */
export interface TraceTokens {
  prompt: number
  cached: number
  completion: number
  cacheShare: number | null
}

export interface TracePerson {
  id: string
  userName: string | null
  displayName: string | null
}

/** Where most of an answer's time went: the step, its share of the whole, and why in words. */
export interface TraceSlowest {
  kind: 'queue' | 'setup' | 'round' | 'tool' | 'agents' | 'agent'
  label: string
  ms: number
  share: number
  detail: string | null
}

export interface AgentTrace {
  index: number
  label: string
  ms: number
  model: string | null
  tokens: TraceTokens
  /** The model's part of its time (the rest: its tool calls). */
  modelMs: number | null
  readPerSecond: number | null
  writePerSecond: number | null
  /** engine: llama.cpp's own timings; clock: worked out from the times and tokens. */
  speedFrom: 'engine' | 'clock' | null
  failed: boolean
  slowest: boolean
  steps: { name: string; ms: number | null; failed: boolean; resultChars: number }[]
}

/** A step of the timeline: the wait in line, getting ready, a round of the model, a tool call, or sub-agents. */
export interface TraceStep {
  kind: 'queue' | 'setup' | 'round' | 'tool' | 'agents'
  label: string
  ms: number
  slowest: boolean
  index: number | null
  status: string | null
  thinkingMs: number | null
  firstTokenMs: number | null
  tokens: TraceTokens | null
  readPerSecond: number | null
  writePerSecond: number | null
  speedFrom: 'engine' | 'clock' | null
  name: string | null
  resultChars: number | null
  files: number | null
  agents: AgentTrace[] | null
}

/** An answer step by step: times, tokens, sizes and tool names, never its words. */
export interface AnswerTrace {
  id: string
  conversationId: string
  at: string
  model: string | null
  status: string
  ms: number
  queueMs: number | null
  setupMs: number | null
  rounds: number
  toolCalls: number
  agents: number
  tokens: TraceTokens
  agentTokens: TraceTokens
  prompt: { kind: string; chars: number; tokens: number | null }[]
  steps: TraceStep[]
  slowest: TraceSlowest | null
  person: TracePerson | null
}

/** An answer in Admin → Traces. */
export interface TraceSummary {
  id: string
  at: string
  person: TracePerson | null
  model: string | null
  status: string
  ms: number
  queueMs: number | null
  rounds: number
  toolCalls: number
  agents: number
  tools: { name: string; count: number; ms: number }[]
  tokens: TraceTokens
  agentTokens: TraceTokens
  slowest: TraceSlowest | null
}

