export interface ChatModel {
  name: string
  context: number | null
  maxOutput: number | null
  vision: boolean
  tools: boolean
  thinking: boolean
  prices: { input: number | null; cachedInput: number | null; output: number | null }
}

export interface ChatConfig {
  model: string | null
  models: ChatModel[]
  presets: { level: string; label: string }[]
  defaultThinking: string | null
  argus: boolean
  gitlabUrl: string | null
  maxUploadBytes: number
  imageTypes: string[]
}

export interface Attachment {
  id: string
  fileName: string
  size: number
  truncated: boolean
  kind: 'text' | 'image'
  contentType: string
}

export interface ToolCall {
  id: string
  function: { name: string; arguments: string }
}

export interface Message {
  id: string
  parentId: string | null
  role: 'user' | 'assistant' | 'tool'
  content: string
  reasoning: string | null
  toolName: string | null
  toolCallId: string | null
  toolCalls: ToolCall[] | null
  attachments: Attachment[]
  status: 'complete' | 'stopped' | 'failed'
  error: string | null
  model: string | null
  promptTokens: number | null
  cachedTokens: number | null
  completionTokens: number | null
  thinkingMs: number | null
  durationMs: number | null
  createdAt: string
  noAccess: boolean
}

export interface ConversationSummary {
  id: string
  title: string
  updatedAt: string
  archivedAt?: string | null
}

export interface Conversation extends ConversationSummary {
  thinking: string | null
  useArgus: boolean
  model: string | null
  systemPrompt: string | null
  temperature: number | null
  topP: number | null
  maxTokens: number | null
  currentLeafId: string | null
  archivedAt: string | null
  /** The chat this one was forked from, while it still exists. */
  forkedFrom: { id: string; title: string } | null
  createdAt: string
  messages: Message[]
}

/** A chat's own settings, as sent to create or change it. */
export interface ChatSettings {
  thinking?: string | null
  useArgus?: boolean
  model?: string | null
  systemPrompt?: string | null
  temperature?: number | null
  topP?: number | null
  maxTokens?: number | null
}

export type ChatEvent =
  | { type: 'question'; id: string; parentId: string | null }
  | { type: 'title'; title: string }
  | { type: 'assistant'; id: string; parentId: string; model: string }
  | { type: 'reasoning'; text: string }
  | { type: 'thought'; ms: number }
  | { type: 'content'; text: string }
  | { type: 'usage'; prompt: number | null; cached: number | null; completion: number | null; thinkingMs: number | null; durationMs: number | null }
  | { type: 'tool_call'; id: string; name: string; arguments: string }
  | { type: 'tool_result'; id: string; messageId: string; name: string; text: string; isError: boolean; noAccess: boolean; durationMs: number }
  | { type: 'notice'; kind: string; text: string }
  | { type: 'error'; message: string }
  | { type: 'done'; id: string }
