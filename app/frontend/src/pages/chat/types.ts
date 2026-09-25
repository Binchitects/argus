export interface ChatModel {
  name: string
  context: number | null
  maxOutput: number | null
  vision: boolean
  tools: boolean
  thinking: boolean
  /** False for an engine model that is not loaded now: it cannot answer until an admin loads it. */
  loaded: boolean
  prices: { input: number | null; cachedInput: number | null; output: number | null }
}

/** A tool this person may use; a chat turns it on or off. */
export interface ChatTool {
  id: string
  title: string
  description: string
  /** search-code, image, calculator, clock, plug */
  icon: string
  onByDefault: boolean
  askFirst: boolean
}

export interface ChatConfig {
  model: string | null
  models: ChatModel[]
  presets: { level: string; label: string }[]
  defaultThinking: string | null
  argus: boolean
  tools: ChatTool[]
  gitlabUrl: string | null
  maxUploadBytes: number
  imageTypes: string[]
}

export interface Attachment {
  id: string
  fileName: string
  size: number
  truncated: boolean
  /** text: read by the model; image: a picture; file: neither (a file Python made), to download. */
  kind: 'text' | 'image' | 'file'
  contentType: string
  /** The file's own bytes are kept (a document's original, a file a tool made): it can be downloaded. */
  original?: boolean
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
  /** declined: a tool call the person did not allow. */
  status: 'complete' | 'stopped' | 'failed' | 'declined'
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
  /** The ids of the tools this chat has on. */
  tools: string[]
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
  /** Tool ids; unset means the tools that are on in new chats. */
  tools?: string[]
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
  | { type: 'tool_call'; id: string; name: string; arguments: string; tool?: string | null }
  | { type: 'approval'; id: string; name: string; arguments: string; tool: string; title: string }
  | { type: 'tool_result'; id: string; messageId: string; name: string; text: string; isError: boolean; declined?: boolean; noAccess: boolean; durationMs: number; attachments?: Attachment[] }
  | { type: 'notice'; kind: string; text: string }
  /** Waiting for a turn: the model serves few at once, in turn (fair use). */
  | { type: 'queued'; ahead: number }
  | { type: 'error'; message: string }
  | { type: 'done'; id: string }
