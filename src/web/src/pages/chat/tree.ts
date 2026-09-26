import type { Message } from './types'

/**
 * A conversation is a tree: an edited question and an answer given again are
 * siblings. What is on screen, and what the model reads, is the path from the
 * first message to the current leaf.
 */
export class ChatTree {
  readonly messages: Message[]
  readonly byId: Map<string, Message>
  private readonly children = new Map<string | null, Message[]>()

  constructor(messages: Message[]) {
    this.messages = messages
    this.byId = new Map(messages.map((m) => [m.id, m]))
    for (const m of messages) {
      const key = m.parentId && this.byId.has(m.parentId) ? m.parentId : null
      const list = this.children.get(key) ?? []
      list.push(m)
      this.children.set(key, list)
    }
  }

  childrenOf(id: string | null): Message[] {
    return this.children.get(id) ?? []
  }

  /** The messages from the first one down to `leaf`. */
  path(leaf: string | null): Message[] {
    const out: Message[] = []
    const seen = new Set<string>()
    for (let m = leaf ? this.byId.get(leaf) : undefined; m && !seen.has(m.id); m = m.parentId ? this.byId.get(m.parentId) : undefined) {
      seen.add(m.id)
      out.push(m)
    }
    return out.reverse()
  }

  /** The newest line of messages below `id` (what switching to it shows). */
  leafBelow(id: string): string {
    let current = id
    for (;;) {
      const kids = this.childrenOf(current)
      if (!kids.length) return current
      current = kids[kids.length - 1]!.id
    }
  }

  /** The alternatives to `m`: itself and the messages with the same parent, oldest first. */
  siblings(m: Message): Message[] {
    return this.childrenOf(m.parentId && this.byId.has(m.parentId) ? m.parentId : null).filter((s) => s.role === m.role)
  }
}

export interface Turn {
  question?: Message
  /** Everything that answered it: assistant messages, tool calls and their results. */
  answer: Message[]
}

/** A path as the page shows it: each question with its answer. */
export function toTurns(path: Message[]): Turn[] {
  const out: Turn[] = []
  for (const m of path) {
    if (m.role === 'user') out.push({ question: m, answer: [] })
    else if (out.length) out[out.length - 1]!.answer.push(m)
    else out.push({ answer: [m] })
  }
  return out
}
