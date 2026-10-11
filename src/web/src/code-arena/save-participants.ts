import type * as Monaco from 'monaco-editor'
import type { SaveReason } from './editor-state'

/** What a step of saving is given: the file's model, its settings (for its language), and why it is saved. */
export interface SaveContext {
  monaco: typeof Monaco
  model: Monaco.editor.ITextModel
  path: string
  settings: Record<string, unknown>
  reason: SaveReason
}

/** A step a file goes through as it is saved: it edits the model (Undo takes it back) or leaves it. */
export interface SaveParticipant {
  (c: SaveContext): void | Promise<void>
  /** What it does, for a message when it fails ("formatting"). */
  what?: string
}

const edit = (c: SaveContext, edits: Monaco.editor.IIdentifiedSingleEditOperation[]) => {
  if (edits.length === 0) return
  c.model.pushStackElement()
  c.model.pushEditOperations([], edits, () => null)
  c.model.pushStackElement()
}

/** "files.trimTrailingWhitespace": spaces and tabs at the lines' ends go (not those at the cursor's line end while typing: this runs on save only). */
export const trimTrailingWhitespace: SaveParticipant = (c) => {
  if (!c.settings['files.trimTrailingWhitespace']) return
  const edits: Monaco.editor.IIdentifiedSingleEditOperation[] = []
  const lines = c.model.getLineCount()
  for (let line = 1; line <= lines; line++) {
    const text = c.model.getLineContent(line)
    const trimmed = text.replace(/[ \t]+$/, '')
    if (trimmed.length !== text.length) edits.push({ range: new c.monaco.Range(line, trimmed.length + 1, line, text.length + 1), text: '' })
  }
  edit(c, edits)
}

/** "files.insertFinalNewline": a file that has text ends with a newline (the file's own line ending). */
export const insertFinalNewline: SaveParticipant = (c) => {
  if (!c.settings['files.insertFinalNewline']) return
  const lines = c.model.getLineCount()
  const last = c.model.getLineContent(lines)
  if (last.length === 0 || (lines === 1 && last.length === 0)) return
  edit(c, [{ range: new c.monaco.Range(lines, last.length + 1, lines, last.length + 1), text: c.model.getEOL() }])
}

/** "files.trimFinalNewlines": the newlines after the last one at the file's end go. */
export const trimFinalNewlines: SaveParticipant = (c) => {
  if (!c.settings['files.trimFinalNewlines']) return
  const lines = c.model.getLineCount()
  let last = lines
  while (last > 1 && c.model.getLineContent(last).length === 0) last--
  // Keep one newline (the line after the last with text, which is empty): take the empty lines after it.
  if (last + 1 >= lines) return
  edit(c, [{ range: new c.monaco.Range(last + 1, 1, lines, 1), text: '' }])
}

/**
 * The steps of saving, in VS Code's order: trim trailing whitespace, (the code actions and format on save, added by
 * them), the final newline, trim final newlines.
 */
export const saveParticipants: SaveParticipant[] = [trimTrailingWhitespace, insertFinalNewline, trimFinalNewlines]

/** Adds a step before the final newline's (format on save goes there). */
export function addSaveParticipant(step: SaveParticipant) {
  saveParticipants.splice(1, 0, step)
  return () => {
    const at = saveParticipants.indexOf(step)
    if (at >= 0) saveParticipants.splice(at, 1)
  }
}
