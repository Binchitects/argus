import { Brain } from 'lucide-react'
import { useState } from 'react'
import { Link } from 'react-router'

/** A memory an answer read: what it said, its kind and whose. */
export interface MemoryRead {
  id: string
  text: string
  kind: string
  scope: string
}

const kinds: Record<string, string> = { preference: 'preference', fact: 'fact', decision: 'decision', howto: 'how to', lesson: 'lesson' }

/** Under an answer: the memories found for its question and given with it, one click to see them. */
export function MemoriesRead({ memories }: { memories: MemoryRead[] }) {
  const [open, setOpen] = useState(false)
  return (
    <div className="mt-2 text-xs text-muted-foreground" role="note" aria-label="Memories it read">
      <button type="button" className="inline-flex items-center gap-1 rounded-sm hover:text-foreground" aria-expanded={open} onClick={() => setOpen((o) => !o)}>
        <Brain className="size-3.5 text-primary-ink" aria-hidden="true" />
        Read {memories.length} {memories.length === 1 ? 'memory' : 'memories'}
      </button>
      {open && (
        <ul className="mt-1 grid gap-0.5 ps-5">
          {memories.map((m) => (
            <li key={m.id} className="list-disc">
              <span className="text-foreground">{m.text}</span> <span>({[kinds[m.kind] ?? m.kind, m.scope === 'company' ? "the company's" : null].filter(Boolean).join(', ')})</span>
            </li>
          ))}
          <li className="list-none">
            <Link to="/memory" className="underline-offset-2 hover:underline">
              All memories
            </Link>
          </li>
        </ul>
      )}
    </div>
  )
}
