/**
 * The Debug Adapter Protocol over a debug session's WebSocket: requests with their own numbers, answered by the
 * response that names them; events to whoever listens; and the adapter's own requests (runInTerminal, startDebugging),
 * which the page always answers, with success false when it cannot.
 */

export interface DapMessage {
  seq: number
  type: 'request' | 'response' | 'event'
  command?: string
  event?: string
  arguments?: unknown
  body?: unknown
  success?: boolean
  message?: string
  request_seq?: number
}

/** A request the adapter refused: its message, and the error's own words when it sent some. */
export class DapError extends Error {
  readonly command: string
  constructor(command: string, message: string) {
    super(message)
    this.name = 'DapError'
    this.command = command
  }
}

type Handler = (body: unknown, message: DapMessage) => void

export class DapClient {
  private seq = 1
  private readonly pending = new Map<number, { command: string; resolve: (body: unknown) => void; reject: (e: Error) => void }>()
  private readonly handlers = new Map<string, Set<Handler>>()
  private readonly socket: WebSocket
  private readonly opened: Promise<void>
  /** Ends when the socket closes (the session ended, or the server let the page go). */
  readonly closed: Promise<void>
  /** The adapter asks the page something (runInTerminal…): what to answer. */
  onRequest: (command: string, args: unknown) => Promise<{ success: boolean; body?: unknown; message?: string }> = async (command) => ({
    success: false,
    message: `Code Arena does not do "${command}".`,
  })

  constructor(url: string) {
    this.socket = new WebSocket(url)
    this.opened = new Promise((resolve, reject) => {
      this.socket.onopen = () => resolve()
      this.socket.onerror = () => reject(new Error('The debug session could not be reached.'))
    })
    this.closed = new Promise((resolve) => {
      this.socket.onclose = () => {
        for (const [, p] of this.pending) p.reject(new DapError(p.command, 'The debug session ended.'))
        this.pending.clear()
        this.emit('codeArena.closed', undefined, { seq: 0, type: 'event', event: 'codeArena.closed' })
        resolve()
      }
    })
    this.socket.onmessage = (e) => {
      if (typeof e.data !== 'string') return
      let m: DapMessage
      try {
        m = JSON.parse(e.data) as DapMessage
      } catch {
        return
      }
      this.receive(m)
    }
  }

  private receive(m: DapMessage) {
    if (m.type === 'response') {
      const p = this.pending.get(m.request_seq ?? -1)
      if (!p) return
      this.pending.delete(m.request_seq!)
      if (m.success) p.resolve(m.body)
      else p.reject(new DapError(p.command, (m.body as { error?: { format?: string } } | undefined)?.error?.format ?? m.message ?? `${p.command} failed.`))
    } else if (m.type === 'event' && m.event) {
      this.emit(m.event, m.body, m)
    } else if (m.type === 'request' && m.command) {
      void this.onRequest(m.command, m.arguments)
        .catch((e: unknown): { success: boolean; body?: unknown; message?: string } => ({ success: false, message: e instanceof Error ? e.message : String(e) }))
        .then((answer) => this.send({ seq: 0, type: 'response', request_seq: m.seq, command: m.command, success: answer.success, body: answer.body, message: answer.message }))
    }
  }

  private emit(event: string, body: unknown, m: DapMessage) {
    for (const h of this.handlers.get(event) ?? []) h(body, m)
    for (const h of this.handlers.get('*') ?? []) h(body, m)
  }

  private send(m: DapMessage) {
    if (this.socket.readyState === WebSocket.OPEN) this.socket.send(JSON.stringify(m))
  }

  /** Waits for the socket to be open. */
  ready() {
    return this.opened
  }

  /** A request to the adapter; its response's body, or a DapError with what it said. */
  request<T = unknown>(command: string, args?: unknown): Promise<T> {
    const seq = this.seq++
    return new Promise<T>((resolve, reject) => {
      this.pending.set(seq, { command, resolve: resolve as (b: unknown) => void, reject })
      if (this.socket.readyState !== WebSocket.OPEN) {
        this.pending.delete(seq)
        reject(new DapError(command, 'The debug session is not connected.'))
        return
      }
      this.socket.send(JSON.stringify({ seq, type: 'request', command, arguments: args }))
    })
  }

  /** Listens to an event ("*": every one); the function stops listening. */
  on(event: string, handler: Handler): () => void {
    let set = this.handlers.get(event)
    if (!set) this.handlers.set(event, (set = new Set()))
    set.add(handler)
    return () => set.delete(handler)
  }

  close() {
    this.socket.close()
  }
}
