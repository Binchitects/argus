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
  /** Auto, offered while a model for small steps is set that the person may use: that model, and whether chats that chose none are on Auto. */
  auto?: { model: string; byDefault: boolean } | null
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
  /** What a message shows beyond what the model read: a tool call's sub-agents' work ({ agents: [...] }), the canvas a call made or changed, an Auto answer's route, a memory to keep ({ memory }). */
  details?: { agents?: AgentWork[]; canvas?: { id: string; title: string; version: number }; route?: AutoRoute; memory?: MemoryOffer } | null
  /** An answer: what filled the request it answered, in characters by kind (the context gauge scales it to its prompt tokens). */
  context?: ContextFill | null
  /** An answer whose thinking was cut short ("Answer now"). */
  cutShort?: boolean
  /** The person's thumbs on this answer (its last message), if they rated it. */
  feedback?: Feedback | null
}

/** What a remember call shows: the memory, its id once kept, and where it stands. */
export interface MemoryOffer {
  id: string | null
  text: string
  /** offered: waiting for the person; kept; declined: offered, and they said no; forgotten: kept, then taken back. */
  state: 'offered' | 'kept' | 'declined' | 'forgotten'
}

/** Why an answer was bad. */
export type FeedbackReason = 'wrong' | 'incomplete' | 'too_long' | 'unsafe' | 'ignored_instructions' | 'other'

/** Thumbs up or down on an answer; a down vote may say why, and share the chat with the admins. */
export interface Feedback {
  up: boolean
  reason: FeedbackReason | null
  comment: string | null
  shared: boolean
}

/** A comparison (arena mode): one question answered by two models, blind until the person votes. */
export interface ArenaMatch {
  id: string
  questionId: string
  /** Each answer's first message, once it started. */
  a: string | null
  b: string | null
  vote: ArenaVote | null
  /** The names, once voted. */
  models: { a: string; b: string } | null
}

/** a or b was better, a tie, or both were bad. */
export type ArenaVote = 'a' | 'b' | 'tie' | 'bad'

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

/** Who answered on Auto, and why: the small model itself, or the chat's main model with a thinking level. */
export interface AutoRoute {
  /** chat, lookup, code, reasoning, research (the small model's word); files, deep, thinking, unknown, unavailable (not asked, or no word). */
  kind: string
  reason: string
  /** The model that answered. */
  model: string
  /** The chat's main model: "Ask the big model" answers again with it. */
  main: string
  thinking: string | null
  small: boolean
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
  assistantId?: string | null
}

export type Reach = 'Private' | 'Groups' | 'Company'

/** An assistant in the gallery: instructions, files and settings a team's chats start with. */
export interface AssistantSummary {
  id: string
  name: string
  description: string | null
  /** A name from assistant-icon.tsx ("bot", "code"…) and a colour ("blue"…). */
  icon: string
  color: string
  /** Who may use it besides its owner and editors. */
  reach: Reach
  owner: string | null
  /** The person's own. */
  mine: boolean
  canEdit: boolean
  /** Chats started with it, by anyone. */
  chats: number
  /** People who used it in the last 30 days. */
  people: number
  /** The person's own chats with it. */
  myChats: number
  files: number
  updatedAt: string
}

export interface Assistant extends Omit<AssistantSummary, 'owner' | 'chats' | 'people' | 'myChats' | 'files'> {
  instructions: string | null
  model: string | null
  thinking: string | null
  /** The tools a new chat with it has on; null: those on in new chats. */
  tools: string[] | null
  starters: string[]
  owner: { id: string; name: string } | null
  canShare: boolean
  createdAt: string
  files: (Attachment & { addedAt: string })[]
  /** The person's own chats with it. */
  chats: ConversationSummary[]
  usage: { chats: number; people: number }
  /** Who it is shared with: for those who may change it. */
  sharing: { groups: Named[]; editorPeople: (Named & { userName: string })[]; editorGroups: Named[] } | null
}

export interface Named {
  id: string
  name: string
}

/** The assistant a chat is with, as the chat shows it. */
export interface ChatAssistant {
  id: string
  name: string
  icon: string
  color: string
  starters: string[]
  /** The person lost access to it: the chat says so, and answers no more with it. */
  noAccess: boolean
}

/** A chat's link, as its owner sees it. */
export interface ChatShare {
  id: string
  reach: Reach
  groups: Named[]
  /** Only the branch that ends at leafId; otherwise the whole chat as it grows. */
  branch: boolean
  leafId: string | null
  opens: number
  people: number
  createdAt: string
}

/** A chat shared with the person, read-only. */
export interface SharedChat {
  id: string
  title: string
  owner: string
  mine: boolean
  branch: boolean
  sharedAt: string
  updatedAt: string
  currentLeafId: string | null
  messages: Message[]
  /** The owner's: the link and the chat, to manage it from here. */
  link: ChatShare | null
  conversationId: string | null
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
  /** The assistant the chat is with, if any. */
  assistant?: ChatAssistant | null
  createdAt: string
  messages: Message[]
  /** The chat's comparisons of two models. */
  arenas?: ArenaMatch[]
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
  /** A new chat with this assistant. */
  assistantId?: string | null
}

export type ChatEvent =
  | { type: 'question'; id: string; parentId: string | null }
  /** The chat's title: the first line, then (with a model for small steps) the one it wrote. */
  | { type: 'title'; title: string; model?: string }
  /** `side`: which answer of a comparison (its model is then "Model A" or "Model B"). */
  | { type: 'assistant'; id: string; parentId: string; model: string; side?: 'a' | 'b' }
  /** Auto: who answers (message `id`), and why. */
  | { type: 'route'; id: string; route: AutoRoute }
  /** A comparison's answer starts: `step` of `of`, one after the other. */
  | { type: 'arena'; id: string; questionId: string; side: 'a' | 'b'; step: number; of: number }
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
  /** The answer is deep research: the page says which step it is on. */
  | { type: 'research' }
  /** Waiting for a turn: the model serves few at once, in turn (fair use). */
  | { type: 'queued'; ahead: number }
  | { type: 'error'; message: string }
  /** The older messages are being summarized (compaction), before an answer or because the person asked. */
  | { type: 'compacting' }
  /** Compacted at message `id`: `covered` messages became `summary`. */
  | { type: 'compacted'; id: string; summary: string; auto: boolean; covered: number; model?: string }
  /** Stopped (by the person, from any tab): what was written is kept. */
  | { type: 'stopped'; id: string | null }
  | { type: 'done'; id: string }
