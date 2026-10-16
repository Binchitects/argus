/**
 * JSON-RPC over a language server's WebSocket, as code-arena relays it: one message per text frame, both ways. Requests
 * have numbers of their own and are answered by the response that names them (a cancelled one is told to the server);
 * notifications go to whoever listens; the server's own requests are always answered, with an error when the page does
 * not do what they ask.
 */

/** A message of the Language Server Protocol: a request, a response or a notification. */
export interface RpcMessage {
  jsonrpc: '2.0'
  id?: number | string | null
  method?: string
  params?: unknown
  result?: unknown
  error?: { code: number; message: string; data?: unknown }
}

/** An error answer: the server's to a request of the page's, or the page's to one of the server's. */
export class ResponseError extends Error {
  readonly code: number
  readonly data: unknown
  constructor(code: number, message: string, data?: unknown) {
    super(message)
    this.name = 'ResponseError'
    this.code = code
    this.data = data
  }
}

/** JSON-RPC's and the protocol's error codes the page uses. */
export const codes = { methodNotFound: -32601, internalError: -32603, requestCancelled: -32800, contentModified: -32801, serverCancelled: -32802 }

/** What a request can be cancelled by: Monaco's cancellation token, as its providers get it. */
export interface Cancellation {
  readonly isCancellationRequested: boolean
  onCancellationRequested(listener: () => void): { dispose(): void }
}

export class LspConnection {
  private next = 1
  private readonly pending = new Map<number, { method: string; resolve: (result: unknown) => void; reject: (e: Error) => void }>()
  private readonly socket: WebSocket
  private ended = false
  private endedWith: (() => void) | null = null
  /** Done once the socket is open; refused when it never opens (code-arena refused it: the server's log says why). */
  readonly opened: Promise<void>
  /** Done once the socket is closed, by either side. */
  readonly closed: Promise<void>
  /** A notification from the server. */
  onNotification: (method: string, params: unknown) => void = () => undefined
  /** A request from the server: its result, or a ResponseError to answer with. */
  onRequest: (method: string, params: unknown) => Promise<unknown> = async (method) => {
    throw new ResponseError(codes.methodNotFound, `Code Arena does not handle ${method}.`)
  }

  constructor(url: string) {
    this.socket = new WebSocket(url)
    this.closed = new Promise((resolve) => {
      this.endedWith = resolve
    })
    this.opened = new Promise((resolve, reject) => {
      this.socket.onopen = () => resolve()
      this.socket.onerror = () => reject(new Error('The language server could not be reached.'))
      void this.closed.then(() => reject(new Error('The language server could not be reached.')))
    })
    // Not left unhandled when nobody waits for it.
    this.opened.catch(() => undefined)
    this.socket.onclose = () => this.end()
    this.socket.onmessage = (e: MessageEvent) => {
      if (typeof e.data !== 'string') return
      let m: RpcMessage
      try {
        m = JSON.parse(e.data) as RpcMessage
      } catch {
        return
      }
      this.receive(m)
    }
  }

  /** Whether the socket is open now. */
  get open() {
    return !this.ended && this.socket.readyState === WebSocket.OPEN
  }

  private end() {
    if (this.ended) return
    this.ended = true
    for (const [, p] of this.pending) p.reject(new ResponseError(codes.internalError, 'The language server ended.'))
    this.pending.clear()
    this.endedWith?.()
  }

  private receive(m: RpcMessage) {
    if (m.method === undefined) {
      if (typeof m.id !== 'number') return
      const p = this.pending.get(m.id)
      if (!p) return
      this.pending.delete(m.id)
      if (m.error) p.reject(new ResponseError(m.error.code, m.error.message, m.error.data))
      else p.resolve(m.result ?? null)
    } else if (m.id === undefined || m.id === null) {
      this.onNotification(m.method, m.params)
    } else {
      const id = m.id
      void this.onRequest(m.method, m.params).then(
        (result) => this.send({ jsonrpc: '2.0', id, result: result ?? null }),
        (e: unknown) =>
          this.send({
            jsonrpc: '2.0',
            id,
            error: e instanceof ResponseError ? { code: e.code, message: e.message } : { code: codes.internalError, message: e instanceof Error ? e.message : String(e) },
          }),
      )
    }
  }

  private send(m: RpcMessage) {
    if (this.open) this.socket.send(JSON.stringify(m))
  }

  /** A request: its result, or a ResponseError (the server's, or that it ended, or that the request was cancelled). */
  request<T = unknown>(method: string, params?: unknown, token?: Cancellation): Promise<T> {
    if (!this.open) return Promise.reject(new ResponseError(codes.internalError, 'The language server is not running.'))
    if (token?.isCancellationRequested) return Promise.reject(new ResponseError(codes.requestCancelled, 'Cancelled.'))
    const id = this.next++
    return new Promise<T>((resolve, reject) => {
      const listening = token?.onCancellationRequested(() => {
        if (!this.pending.delete(id)) return
        this.notify('$/cancelRequest', { id })
        reject(new ResponseError(codes.requestCancelled, 'Cancelled.'))
      })
      const done = () => listening?.dispose()
      this.pending.set(id, {
        method,
        resolve: (r) => {
          done()
          resolve(r as T)
        },
        reject: (e) => {
          done()
          reject(e)
        },
      })
      this.send({ jsonrpc: '2.0', id, method, params })
    })
  }

  notify(method: string, params?: unknown) {
    this.send({ jsonrpc: '2.0', method, params })
  }

  /** Closes the socket: code-arena stops the server (shutdown, exit). */
  close() {
    try {
      this.socket.close()
    } finally {
      this.end()
    }
  }
}
