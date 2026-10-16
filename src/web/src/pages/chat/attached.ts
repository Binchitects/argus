/**
 * What went with a question asked in Code Arena (@files, the lines chosen in its editor): after what was typed, between
 * these markers, the files and selections as the model read them. The web shows what was typed, and what went with it
 * as chips (as Code Arena's IDE does: src/CodeArena/Mentions.cs, Typed and Attached).
 */
const marker = '\n\n<attached>'

const block = /<(file|folder|selection) path="([^"]*)"(?: lines="(\d+-\d+)")?/y

/** What the person typed of a message (what went with it left out). */
export function typedOf(content: string): string {
  const at = content.indexOf(marker)
  return at < 0 ? content : content.slice(0, at)
}

/** Lines of a file as the person reads them: path:first-last, path:line for one. */
function label(path: string, lines: string): string {
  const [first, last] = lines.split('-')
  return first === last ? `${path}:${first}` : `${path}:${lines}`
}

/**
 * What went with a message: each file, folder or selection as path, or path:first-last. Read block by block (a block is
 * skipped to its end), so text inside an attached file that looks like a block is not one.
 */
export function attachedOf(content: string): string[] {
  const at = content.indexOf(marker)
  if (at < 0) return []
  const found: string[] = []
  let from = at + marker.length
  while (content[from] === '\n') {
    block.lastIndex = from + 1
    const m = block.exec(content)
    if (!m) break
    const [, kind, path, lines] = m
    const name = lines ? label(path!, lines) : path!
    if (!found.includes(name)) found.push(name)
    const close = `\n</${kind}>`
    const end = content.indexOf(close, m.index)
    // A binary file's block closes on its own line.
    const inline = content.indexOf(`</${kind}>`, m.index)
    const nl = content.indexOf('\n', m.index)
    if (inline >= 0 && (end < 0 || inline < end) && (nl < 0 || inline < nl)) {
      from = inline + kind!.length + 3
      continue
    }
    if (end < 0) break
    from = end + close.length
  }
  return found
}
