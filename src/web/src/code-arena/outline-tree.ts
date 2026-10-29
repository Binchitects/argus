import type { OutlineSymbol } from './ide-api'

export interface Node {
  symbol: OutlineSymbol
  children: Node[]
}

/** The symbols nested by what holds each (Namespace › Class › method), in the order they come. */
export function nest(symbols: OutlineSymbol[]): Node[] {
  const roots: Node[] = []
  const byName = new Map<string, Node>()
  for (const s of [...symbols].sort((a, b) => a.start - b.start || b.end - a.end)) {
    const node = { symbol: s, children: [] }
    const parent = s.parent ? byName.get(s.parent) : undefined
    if (parent && parent.symbol.start <= s.start && s.end <= parent.symbol.end) parent.children.push(node)
    else roots.push(node)
    if (!byName.has(s.qualified)) byName.set(s.qualified, node)
  }
  return roots
}

/** The innermost symbol holding a line: where the cursor is. */
export function innermost(nodes: Node[], line: number): OutlineSymbol | null {
  for (const n of nodes) {
    if (n.symbol.start <= line && line <= n.symbol.end) return innermost(n.children, line) ?? n.symbol
  }
  return null
}
