import type * as Monaco from 'monaco-editor'
import { ApiError } from '@/lib/api'
import type { MonacoModule } from './editor-state'
import { completeCode } from './ide-api'

// Code completion in the editor: the code around the cursor goes to code-arena (POST /api/complete), which asks the
// model to fill in the middle; what comes back shows as grey text, Tab takes it (Monaco's inline suggestions).

/** Whether completions are asked for now: on in code-arena's config and the person's, and no answer being written. */
export const completion = { wanted: () => false }

/** How long typing must pause before a completion is asked for. */
export const pause = 300

/** After the key's limit is reached, how long completions wait before asking again. */
const backOff = 30_000

let installed = false
let resting = 0
let last: { key: string; text: string } | null = null

const wait = (ms: number, token: Monaco.CancellationToken) =>
  new Promise<void>((done) => {
    const timer = setTimeout(done, ms)
    token.onCancellationRequested(() => {
      clearTimeout(timer)
      done()
    })
  })

/** The provider, once for every editor: Monaco asks it as the person types, and cancels what the next key makes stale. */
export function installCompletions(m: MonacoModule) {
  if (installed) return
  installed = true
  m.monaco.languages.registerInlineCompletionsProvider(
    { pattern: '**' },
    {
      provideInlineCompletions: async (model, position, _context, token) => {
        const none = { items: [] }
        const path = m.pathOf(model.uri)
        if (!path || !completion.wanted() || Date.now() < resting || model.getValueLength() > 1_000_000) return none
        await wait(pause, token)
        if (token.isCancellationRequested) return none
        const text = model.getValue()
        const at = model.getOffsetAt(position)
        const prefix = text.slice(Math.max(0, at - 6_000), at)
        const suffix = text.slice(at, at + 2_000)
        // Not after a blank: nothing typed to go on.
        if (!prefix.trim()) return none
        const key = `${path}\0${prefix}\0${suffix}`
        let found = last?.key === key ? last.text : null
        if (found === null) {
          const stop = new AbortController()
          const listening = token.onCancellationRequested(() => stop.abort())
          try {
            found = (await completeCode({ path, prefix, suffix }, stop.signal)).text
            last = { key, text: found }
          } catch (e) {
            if (e instanceof ApiError && e.http === 429) resting = Date.now() + backOff
            return none
          } finally {
            listening.dispose()
          }
        }
        if (!found || token.isCancellationRequested) return none
        return { items: [{ insertText: found, range: new m.monaco.Range(position.lineNumber, position.column, position.lineNumber, position.column) }] }
      },
      disposeInlineCompletions: () => undefined,
    },
  )
}
