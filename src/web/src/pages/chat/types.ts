export interface ChatModel {
  name: string
  context: number | null
  maxOutput: number | null
  vision: boolean
  tools: boolean
  thinking: boolean
  /** False for an engine model that is not loaded now. */
  loaded: boolean
  /** Not loaded, but loads when asked for (the engine has a place beside the models kept loaded): its first answer waits. */
  onRequest?: boolean
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
  /** text: read by the model; image: a picture; audio, video: played in place; file: neither (a file Python made), to download. */
  kind: 'text' | 'image' | 'audio' | 'video' | 'file'
  contentType: string
  /** The file's own bytes are kept (a document's original, a file a tool made): it can be downloaded. */
  original?: boolean
  /** A sound's or video's length. */
  seconds?: number | null
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
  /** The chat was compacted here: the model reads this summary instead of the branch down to this message. */
  summary?: string | null
  /** What a tool call shows beyond what the model read: sub-agents' work ({ agents: [...] }), the canvas it made or changed. */
  details?: { agents?: AgentWork[]; canvas?: { id: string; title: string; version: number } } | null
  /** An answer: what filled the request it answered, in characters by kind (the context gauge scales it to its prompt tokens). */
  context?: ContextFill | null
  /** An answer whose thinking was cut short ("Answer now"). */
  cutShort?: boolean
}

export interface ContextFill {
  system: number
  instructions: number
  tools: number
  summary: number
  files: number
  you: number
  answers: number
  toolResults: number
}

/** A sub-agent's work, as it happens and as it is kept: its part, thinking, tool calls with their results, and words. */
export interface AgentWork {
  title: string
  instructions: string
  reasoning: string
  text: string
  /** Its tool calls; `files`: what a call made (pictures, a Python run's files), the chat's files too. */
  steps: { id: string; name: string; arguments: string; result?: string; isError?: boolean; files?: Attachment[] | null }[]
  /** running while it works; then done, or failed with its error. */
  status?: 'running' | 'done' | 'failed'
  error: string | null
  ms: number | null
  /** The model it ran on, and the tokens it used (once done): the answer's cost counts them. */
  model?: string | null
  usage?: TokenUsage | null
}

export interface TokenUsage {
  prompt: number
  cached: number
  completion: number
}

export interface ConversationSummary {
  id: string
  title: string
  updatedAt: string
  archivedAt?: string | null
  /** An answer is being written (it goes on when the page closes): the page watches it again. */
  answering?: boolean
  projectId?: string | null
}

/** A project: chats together, with instructions and files every answer in them reads. */
export interface ProjectSummary {
  id: string
  name: string
  description: string | null
  updatedAt: string
  chats: number
  files: number
}

export interface Project {
  id: string
  name: string
  description: string | null
  instructions: string | null
  createdAt: string
  updatedAt: string
  files: (Attachment & { addedAt: string })[]
  chats: ConversationSummary[]
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
  /** The project the chat is in, if any. */
  project?: { id: string; name: string } | null
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
  /** A new chat made in this project. */
  projectId?: string | null
}

export type ChatEvent =
  | { type: 'question'; id: string; parentId: string | null }
  | { type: 'title'; title: string }
  | { type: 'assistant'; id: string; parentId: string; model: string }
  | { type: 'reasoning'; text: string }
  | { type: 'thought'; ms: number; cutShort?: boolean }
  | { type: 'content'; text: string }
  | { type: 'usage'; prompt: number | null; cached: number | null; completion: number | null; thinkingMs: number | null; durationMs: number | null; context?: ContextFill | null }
  | { type: 'tool_call'; id: string; name: string; arguments: string; tool?: string | null }
  | { type: 'approval'; id: string; name: string; arguments: string; tool: string; title: string }
  /** How far a long tool call is, as its server says (`total` when it knows the end). */
  | { type: 'tool_progress'; id: string; progress: number; total: number | null; message: string | null }
  | { type: 'tool_result'; id: string; messageId: string; name: string; text: string; isError: boolean; declined?: boolean; noAccess: boolean; durationMs: number; attachments?: Attachment[]; details?: Message['details'] }
  /** A sub-agent's step (the delegate tool's call `id`, the agent's `index`): started, thinking or words as they come, a tool call, its result, done. */
  | {
      type: 'agent'
      id: string
      index: number
      event: 'start' | 'reasoning' | 'content' | 'tool_call' | 'tool_result' | 'done'
      title?: string
      instructions?: string
      text?: string | null
      call?: { id: string; name?: string; arguments?: string } | null
      isError?: boolean | null
      /** What a tool call made (with its tool_result). */
      files?: Attachment[] | null
      error?: string | null
      ms?: number | null
      /** With done: the model it ran on and the tokens it used. */
      model?: string | null
      usage?: TokenUsage | null
    }
  | { type: 'notice'; kind: string; text: string }
  /** Waiting for a turn: the model serves few at once, in turn (fair use). */
  | { type: 'queued'; ahead: number }
  | { type: 'error'; message: string }
  /** The older messages are being summarized (compaction), before an answer or because the person asked. */
  | { type: 'compacting' }
  /** Compacted at message `id`: `covered` messages became `summary`. */
  | { type: 'compacted'; id: string; summary: string; auto: boolean; covered: number }
  /** Stopped (by the person, from any tab): what was written is kept. */
  | { type: 'stopped'; id: string | null }
  | { type: 'done'; id: string }
