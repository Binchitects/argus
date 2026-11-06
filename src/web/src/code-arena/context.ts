import { contextOf, type ContextView } from '@/pages/chat/context'
import type { Message } from '@/pages/chat/types'
import type { CodeState } from './api'

/**
 * How full the window is, as the IDE shows it in the chat's ring and in the status bar's Context: while no turn streams,
 * Code Arena's measure of the next request (what fills it now, the note it adds and an answer written in the web chat
 * included, scaled to the tokens it holds); during a turn, each step's request as the model counted it. `path`: the
 * session's messages in order.
 */
export function codeContext(path: Message[], state: CodeState, streaming = false): ContextView | undefined {
  const last = path.findLast((m) => m.role === 'assistant' && m.promptTokens != null)
  // The whole conversation stays: the last message a compaction covers carries its summary.
  const at = path.findLastIndex((m) => m.summary)
  const compacted = at >= 0 && (!last || at >= path.indexOf(last)) ? path[at]!.summary : null
  const now = state.window && (!streaming || !last?.context) ? { fill: state.window, tokens: state.contextUsed } : undefined
  return contextOf(last, state.context, state.reserve ?? null, compacted, now)
}

/** Opens the chat's Context panel (the status bar's Context asks for it: the panel is the chat's). */
export const contextEvent = 'code-arena:context'
