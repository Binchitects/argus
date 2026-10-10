// The way from the editor (and the Explorer) to the agent's chat: lines of a file to send with the next message, or
// a question about them sent at once. The chat sets it while it is there.

/** Lines of a file sent with a message, from 1; `text`: the editor's, when the file has unsaved changes. */
export interface Piece {
  path: string
  startLine: number
  endLine: number
  text?: string
}

export interface ChatBridgeApi {
  /** Lines to go with the next message: shown above the box, which takes the focus. */
  attach: (piece: Piece) => void
  /** A question about these lines, sent now (queued while an answer is written). */
  ask: (text: string, pieces: Piece[]) => void
  /** A file or folder named in the box (@path): the whole of it goes with the message. */
  mention: (path: string) => void
}

/** The chat's, while it is open in the page; null before. */
export const chatBridge: { current: ChatBridgeApi | null } = { current: null }

/** How lines read: path:first-last (path:line for one), as code-arena lists what went with a message. */
export const pieceLabel = (p: Piece) => `${p.path}:${p.startLine === p.endLine ? p.startLine : `${p.startLine}-${p.endLine}`}`

/** What asking about code from the editor says, with the lines it is about. */
export const asks = {
  explain: 'Explain this code: what it does, and how it fits with the code around it.',
  fix: (problems: string[]) =>
    problems.length > 0 ? `Fix the problems in this code: ${problems.join('; ')}.` : 'Find the bugs in this code and fix them.',
  complete: "Complete this code: write what is missing (a function's body, a TODO) with edit_file, keeping its signature and the file's style.",
}

/** How a problem the check found reads to the agent: line:column, its code and message. */
export const problemText = (p: { line: number; column: number; code: string | null; message: string }) =>
  `${p.line}:${p.column} ${p.code ? `${p.code} ` : ''}${p.message.replace(/\.$/, '')}`
