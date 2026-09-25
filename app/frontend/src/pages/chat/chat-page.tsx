import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Archive, ArrowDown, Code2, FileUp, GitFork, Lightbulb, Search, Sparkles } from 'lucide-react'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router'
import { PageSkeleton, QueryError } from '@/components/app/query-state'
import { Alert } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Sheet, SheetContent, SheetDescription, SheetTitle } from '@/components/ui/sheet'
import { toast } from '@/components/ui/toaster'
import { api, ApiError, errorMessage, infoQuery } from '@/lib/api'
import { useMedia } from '@/lib/use-media'
import { cn } from '@/lib/utils'
import { archiveChat, configQuery, conversationQuery, forkChat, streamChat } from './api'
import { Composer } from './composer'
import { collectFiles } from './files'
import { FilesPanel } from './files-panel'
import { ChatHeader } from './header'
import { reduce, stopped, withQuestion, type LiveState } from './live'
import { QuestionRail } from './question-rail'
import { toolsOn } from './tools'
import { ToolsPicker } from './tools-picker'
import { ChatList } from './sidebar'
import { ChatTree, toTurns } from './tree'
import { AnswerTurn, QuestionTurn } from './turns'
import type { ChatConfig, ChatSettings, Conversation, Message } from './types'
import { useUploads } from './uploads'

export function ChatPage() {
  const { id } = useParams()
  const navigate = useNavigate()
  const config = useQuery(configQuery)
  const [listOpen, setListOpen] = useState(false)
  // A new chat gets its id with its first message; the thread must survive that
  // navigation (it is mid-answer), so it is keyed by "which new chat" until then.
  const [fresh, setFresh] = useState(0)
  const [adopted, setAdopted] = useState<string | null>(null)
  const threadKey = !id || id === adopted ? `new-${fresh}` : id
  const startNew = () => {
    setFresh((n) => n + 1)
    setAdopted(null)
    setListOpen(false)
    navigate('/chat')
  }

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if ((e.metaKey || e.ctrlKey) && e.shiftKey && e.key.toLowerCase() === 'o') {
        e.preventDefault()
        startNew()
      }
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  })

  return (
    <div className="grid h-[calc(100dvh-3.5rem)] min-h-0 lg:grid-cols-[16rem_minmax(0,1fr)] 2xl:grid-cols-[19rem_minmax(0,1fr)]">
      <div className="hidden min-h-0 border-r bg-sidebar lg:block">
        <ChatList activeId={id} onNew={startNew} />
      </div>
      <Sheet open={listOpen} onOpenChange={setListOpen}>
        <SheetContent side="left" className="w-80 gap-0 bg-sidebar p-0">
          <SheetTitle className="sr-only">Chats</SheetTitle>
          <SheetDescription className="sr-only">Your chats</SheetDescription>
          <ChatList activeId={id} onNew={startNew} onNavigate={() => setListOpen(false)} />
        </SheetContent>
      </Sheet>
      {config.error ? (
        <div className="p-6">
          <QueryError error={config.error} retry={() => config.refetch()} />
        </div>
      ) : config.data ? (
        <Thread key={threadKey} id={id} config={config.data} onAdopt={setAdopted} onOpenList={() => setListOpen(true)} />
      ) : (
        <div className="p-6">
          <PageSkeleton />
        </div>
      )}
    </div>
  )
}

const suggestions = [
  { icon: Code2, text: 'Write a Python script that renames photos by the date they were taken.' },
  { icon: Lightbulb, text: 'Explain the difference between a mutex and a semaphore, with a C example.' },
  { icon: Sparkles, text: 'Review this approach: caching API responses in Redis for five minutes.' },
]

function Thread({ id, config, onAdopt, onOpenList }: { id?: string; config: ChatConfig; onAdopt: (id: string) => void; onOpenList: () => void }) {
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const loaded = useQuery({ ...conversationQuery(id ?? ''), enabled: !!id })
  const brand = useQuery(infoQuery).data?.name
  const [draft, setDraft] = useState<ChatSettings>({})
  const [live, setLive] = useState<LiveState | null>(null)
  const liveRef = useRef<LiveState | null>(null)
  useEffect(() => {
    liveRef.current = live
  }, [live])
  const wide = useMedia('(min-width: 1280px)')
  const [streaming, setStreaming] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const abort = useRef<AbortController | null>(null)
  /** The chat an answer is streaming in: known before the route says so, when a new chat's first answer starts. */
  const streamingIn = useRef<string | null>(null)
  const uploads = useUploads(config.maxUploadBytes)
  const [filesOpen, setFilesOpen] = useState(false)
  const [selectedFile, setSelectedFile] = useState<string | null>(null)
  const [dragging, setDragging] = useState(false)
  const scroller = useRef<HTMLDivElement>(null)
  const [atBottom, setAtBottom] = useState(true)
  const [readingQuestion, setReadingQuestion] = useState<string | null>(null)
  /** The thread's scrollbar width: the question rail sits beside it, not under it. */
  const [gutter, setGutter] = useState(0)
  /** A question jumped to stays marked until the person scrolls or asks again (a short chat is always "at the end"). */
  const pinned = useRef<string | null>(null)
  const questionCount = useRef(0)
  /** The thread's scroller. Scrolling it by hand (not a jump's smooth scroll) lets the rail follow the view again. */
  const thread = useCallback((el: HTMLDivElement | null) => {
    scroller.current = el
    if (!el) return
    const unpin = () => {
      pinned.current = null
    }
    const events = ['wheel', 'touchmove', 'keydown'] as const
    for (const type of events) el.addEventListener(type, unpin, { passive: true })
    return () => {
      for (const type of events) el.removeEventListener(type, unpin)
    }
  }, [])

  const data = loaded.data
  const settings: ChatSettings = data
    ? { model: data.model, thinking: data.thinking, tools: data.tools, systemPrompt: data.systemPrompt, temperature: data.temperature, topP: data.topP, maxTokens: data.maxTokens }
    : draft
  const view: LiveState = live ?? { messages: data?.messages ?? [], leaf: data?.currentLeafId ?? null, notices: [], title: null, thinkingSince: null }
  const tree = useMemo(() => new ChatTree(view.messages), [view.messages])
  const path = useMemo(() => tree.path(view.leaf), [tree, view.leaf])
  const turns = toTurns(path)
  const files = useMemo(() => collectFiles(path), [path])
  const model = config.models.find((m) => m.name === settings.model) ?? config.models[0]
  const toolsOnHere = toolsOn(config.tools, settings.tools)
  const title = live?.title ?? data?.title ?? null

  useEffect(() => {
    document.title = title ? `${title} · ${brand ?? 'Chat'}` : `Chat · ${brand ?? ''}`.trim()
  }, [title, brand])

  /**
   * The question being read: at the end of the thread, the last one (a question
   * just sent is short of the top of the view); otherwise the last one whose top
   * has passed the top of the view.
   */
  const updateReading = useCallback(() => {
    const el = scroller.current
    if (!el) return
    setGutter(el.offsetWidth - el.clientWidth)
    const questions = [...el.querySelectorAll<HTMLElement>('[data-question]')]
    if (questions.length > questionCount.current) pinned.current = null
    questionCount.current = questions.length
    if (pinned.current) {
      setReadingQuestion(pinned.current)
      return
    }
    if (el.scrollHeight - el.scrollTop - el.clientHeight < 80) {
      setReadingQuestion(questions.at(-1)?.dataset.question ?? null)
      return
    }
    const line = el.getBoundingClientRect().top + 96
    let reading = questions[0]?.dataset.question ?? null
    for (const q of questions) {
      if (q.getBoundingClientRect().top > line) break
      reading = q.dataset.question ?? null
    }
    setReadingQuestion(reading)
  }, [])

  // Follow the answer as it grows, unless the person scrolled up to read.
  useEffect(() => {
    const el = scroller.current
    if (atBottom && el) el.scrollTop = el.scrollHeight
    updateReading()
  }, [view.messages, atBottom, updateReading])

  useEffect(() => {
    window.addEventListener('resize', updateReading)
    return () => window.removeEventListener('resize', updateReading)
  }, [updateReading])

  const onScroll = () => {
    const el = scroller.current
    if (!el) return
    setAtBottom(el.scrollHeight - el.scrollTop - el.clientHeight < 80)
    updateReading()
  }

  const jumpTo = (questionId: string) => {
    const target = scroller.current?.querySelector<HTMLElement>(`[data-question="${questionId}"]`)
    if (!target) return
    setAtBottom(false)
    pinned.current = questionId
    setReadingQuestion(questionId)
    target.scrollIntoView({ behavior: 'smooth', block: 'start' })
    target.focus({ preventScroll: true })
  }

  const forkFrom = async (messageId: string) => {
    if (!id) return
    try {
      const made = await forkChat(id, messageId)
      void queryClient.invalidateQueries({ queryKey: ['chat', 'list'] })
      navigate(`/chat/${made.id}`)
      toast.success(`Forked into “${made.title}”`)
    } catch (e) {
      toast.error(errorMessage(e))
    }
  }

  /** Yes or no to a tool call waiting for the person. */
  const decide = async (callId: string, allow: boolean) => {
    const chat = streamingIn.current ?? id
    if (!chat) return
    setLive((s) => (s ? { ...s, waiting: (s.waiting ?? []).filter((w) => w !== callId) } : s))
    await api(`/api/chat/conversations/${chat}/tool-calls/${encodeURIComponent(callId)}`, { body: { allow } }).catch((e) => toast.error(errorMessage(e)))
  }

  const unarchive = async () => {
    if (!id) return
    await archiveChat(id, false).catch((e) => toast.error(errorMessage(e)))
    await queryClient.invalidateQueries({ queryKey: ['chat'] })
  }

  const change = async (c: ChatSettings) => {
    if (!id) {
      setDraft((d) => ({ ...d, ...Object.fromEntries(Object.entries(c).map(([k, v]) => [k, v === '' || (typeof v === 'number' && v < 0) ? null : v])) }))
      return
    }
    try {
      await api(`/api/chat/conversations/${id}`, { method: 'PATCH', body: c })
      await queryClient.invalidateQueries({ queryKey: conversationQuery(id).queryKey })
    } catch (e) {
      toast.error(errorMessage(e))
    }
  }

  const toolsPicker = model?.tools === false ? null : <ToolsPicker tools={config.tools} value={toolsOnHere} onChange={(tools) => void change({ tools })} />

  const rename = async (t: string) => {
    if (!id) return
    await api(`/api/chat/conversations/${id}`, { method: 'PATCH', body: { title: t } }).catch((e) => toast.error(errorMessage(e)))
    await queryClient.invalidateQueries({ queryKey: ['chat'] })
  }

  /** Streams one answer; false when the question never reached the server (it goes back into the box). */
  /** Which run is current: a run that ended tidies up only while no newer one has started. */
  const runs = useRef(0)
  const run = useCallback(
    async (conversationId: string, endpoint: string, body: object, start: LiveState, localId: string | null): Promise<boolean> => {
      const me = ++runs.current
      setLive(start)
      setStreaming(true)
      streamingIn.current = conversationId
      setError(null)
      setAtBottom(true)
      const controller = new AbortController()
      abort.current = controller
      let received = false
      let wasStopped = false
      try {
        await streamChat(
          `/api/chat/conversations/${conversationId}/${endpoint}`,
          body,
          (e) => {
            received = true
            setLive((s) => reduce(s ?? start, e, localId))
          },
          controller.signal,
        )
      } catch (err) {
        wasStopped = err instanceof DOMException && err.name === 'AbortError'
        if (!wasStopped)
          setError(err instanceof ApiError ? err.message : received ? 'The answer was interrupted.' : 'The message did not reach the server. It is back in the box below: send it again.')
      } finally {
        setStreaming(false)
        abort.current = null
        if (wasStopped) setLive((s) => (s ? stopped(s) : s))
        void queryClient.invalidateQueries({ queryKey: ['chat', 'list'] })
        // The server's copy is the truth (ids, statuses, what a stop kept). After a
        // stop it saves a moment after the stream ends: wait until it has the leaf.
        const leaf = liveRef.current?.leaf
        for (let i = 0; i < 20 && runs.current === me; i++) {
          await queryClient.invalidateQueries({ queryKey: conversationQuery(conversationId).queryKey })
          const saved = queryClient.getQueryData<Conversation>(conversationQuery(conversationId).queryKey)
          if (!received || !leaf || leaf.startsWith('local-') || saved?.messages.some((m) => m.id === leaf)) break
          await new Promise((r) => setTimeout(r, 300))
        }
        // "Answer again" right after a stop starts a new run while this one waits:
        // clearing now would wipe the new answer off the screen as it streams.
        if (runs.current === me) setLive(null)
      }
      return received || wasStopped
    },
    [queryClient],
  )

  /** The chat's id, making the chat first when this is its first message. */
  const ensureChat = async (): Promise<string | null> => {
    if (id) return id
    try {
      const created = await api<Conversation>('/api/chat/conversations', { body: draft })
      queryClient.setQueryData(conversationQuery(created.id).queryKey, { ...created, messages: [] })
      onAdopt(created.id)
      navigate(`/chat/${created.id}`, { replace: true })
      return created.id
    } catch (e) {
      setError(errorMessage(e, 'The chat could not be created. Check the connection and send again.'))
      return null
    }
  }

  const send = async (text: string): Promise<boolean> => {
    const conversationId = await ensureChat()
    if (!conversationId) return false
    const parent = view.leaf
    const localId = `local-${Date.now()}`
    const attachments = uploads.attachments
    const ok = await run(conversationId, 'messages', { content: text, attachments: attachments.map((a) => a.id), parentId: parent ?? undefined, root: parent === null }, withQuestion(view, localId, parent, text, attachments), localId)
    if (ok) uploads.clear()
    return ok
  }

  const edit = (m: Message, text: string) => {
    if (!id) return
    const localId = `local-${Date.now()}`
    void run(id, 'messages', { content: text, attachments: m.attachments.map((a) => a.id), parentId: m.parentId ?? undefined, root: m.parentId === null }, withQuestion(view, localId, m.parentId, text, m.attachments), localId)
  }

  const regenerate = (question: Message, overrides?: { model?: string; thinking?: string }) => {
    if (!id) return
    void run(id, 'regenerate', { messageId: question.id, ...overrides }, { ...view, leaf: question.id, notices: [] }, null)
  }

  const switchTo = async (messageId: string) => {
    if (!id || streaming) return
    const leaf = tree.leafBelow(messageId)
    queryClient.setQueryData<Conversation>(conversationQuery(id).queryKey, (c) => (c ? { ...c, currentLeafId: leaf } : c))
    await api(`/api/chat/conversations/${id}/leaf`, { method: 'PUT', body: { messageId } }).catch((e) => toast.error(errorMessage(e)))
  }

  const openFile = (name: string) => {
    const f = [...files].reverse().find((x) => x.name === name)
    setSelectedFile(f?.key ?? null)
    setFilesOpen(true)
  }

  if (id && loaded.isPending) return <div className="p-6"><PageSkeleton /></div>
  if (id && loaded.error) return <div className="p-6"><QueryError error={loaded.error} retry={() => loaded.refetch()} /></div>

  const empty = turns.length === 0
  const lastTurn = turns.length - 1
  const panel = <FilesPanel files={files} selected={selectedFile} onSelect={setSelectedFile} onClose={() => setFilesOpen(false)} />

  return (
    <div
      className={cn('relative grid min-h-0 min-w-0', filesOpen && wide && 'grid-cols-[minmax(0,1fr)_26rem] 2xl:grid-cols-[minmax(0,1fr)_34rem]')}
      onDragEnter={(e) => {
        if (e.dataTransfer.types.includes('Files')) {
          e.preventDefault()
          setDragging(true)
        }
      }}
      onDragOver={(e) => e.dataTransfer.types.includes('Files') && e.preventDefault()}
      onDragLeave={(e) => !e.currentTarget.contains(e.relatedTarget as Node) && setDragging(false)}
      onDrop={(e) => {
        e.preventDefault()
        setDragging(false)
        if (e.dataTransfer.files.length) uploads.add(e.dataTransfer.files)
      }}
    >
      <div className="flex min-h-0 min-w-0 flex-col">
        <ChatHeader
          config={config}
          settings={settings}
          title={id ? title : null}
          onRename={rename}
          onChange={change}
          filesCount={files.length}
          filesOpen={filesOpen}
          onToggleFiles={() => setFilesOpen(!filesOpen)}
          onOpenList={onOpenList}
          chat={id && data ? { id, title: title ?? data.title, archived: !!data.archivedAt } : undefined}
        />
        {empty ? (
          <div className="flex min-h-0 flex-1 flex-col items-center justify-center overflow-y-auto px-4 py-8">
            <div className="w-full max-w-2xl animate-enter">
              <h1 className="mb-2 text-center text-2xl font-semibold tracking-tight">What can I help with?</h1>
              <p className="mb-6 text-center text-muted-foreground">
                {toolsOnHere.includes('argus') ? 'Ask anything. Argus searches the code you have access to in GitLab.' : 'Ask anything, or attach documents, spreadsheets, slides, PDFs, code and images to ask about them.'}
              </p>
              {error && (
                <Alert variant="destructive" className="mb-3">
                  {error}
                </Alert>
              )}
              <Composer streaming={streaming} onSend={send} onStop={() => abort.current?.abort()} uploads={uploads} model={model} tools={toolsPicker} autoFocus big />
              <div className="stagger mt-4 grid gap-2 sm:grid-cols-3">
                {(config.argus ? [{ icon: Search, text: 'Which of our repositories call the payment service, and where?' }, ...suggestions.slice(0, 2)] : suggestions).map((s) => (
                  <button
                    key={s.text}
                    type="button"
                    disabled={streaming}
                    onClick={() => void send(s.text)}
                    className="flex items-start gap-2 rounded-xl border bg-card p-3 text-left text-sm text-muted-foreground transition-[color,border-color,box-shadow,translate] duration-200 outline-none hover:-translate-y-0.5 hover:border-primary/40 hover:text-foreground hover:shadow-md focus-visible:ring-[3px] focus-visible:ring-ring"
                  >
                    <s.icon className="mt-0.5 size-4 shrink-0 text-primary" aria-hidden="true" />
                    {s.text}
                  </button>
                ))}
              </div>
            </div>
          </div>
        ) : (
          <>
            <div className="relative flex min-h-0 flex-1 flex-col">
              <div ref={thread} onScroll={onScroll} className="relative min-h-0 flex-1 overflow-y-auto" aria-live="polite">
                <div className="mx-auto grid w-full max-w-(--thread-max) gap-6 px-4 py-6 sm:px-6">
                  {data?.archivedAt && (
                    <output className="flex flex-wrap items-center gap-2 rounded-lg border bg-muted/40 px-3 py-2 text-sm">
                      <Archive className="size-4 text-muted-foreground" aria-hidden="true" />
                      <span className="flex-1">This chat is archived. Writing in it brings it back to the list.</span>
                      <Button variant="outline" size="sm" className="h-7" onClick={() => void unarchive()}>
                        Unarchive
                      </Button>
                    </output>
                  )}
                  {data?.forkedFrom && (
                    <p className="-mb-2 flex min-w-0 items-center gap-1.5 text-xs text-muted-foreground">
                      <GitFork className="size-3.5 shrink-0" aria-hidden="true" /> Forked from
                      <Link to={`/chat/${data.forkedFrom.id}`} className="truncate font-medium text-foreground underline-offset-2 hover:underline">
                        {data.forkedFrom.title}
                      </Link>
                    </p>
                  )}
                  {turns.map((t, i) => (
                    <div key={t.question?.id ?? t.answer[0]?.id ?? i} className="grid gap-4">
                      {t.question && <QuestionTurn m={t.question} siblings={tree.siblings(t.question)} busy={streaming} onSwitch={switchTo} onEdit={edit} />}
                      {(t.answer.length > 0 || (streaming && i === lastTurn)) && (
                        <AnswerTurn
                          answer={t.answer}
                          siblings={t.answer[0] ? tree.siblings(t.answer[0]) : []}
                          live={streaming && i === lastTurn}
                          thinkingSince={streaming && i === lastTurn ? view.thinkingSince : null}
                          notices={i === lastTurn ? view.notices : []}
                          config={config}
                          question={t.question}
                          onSwitch={switchTo}
                          onRegenerate={regenerate}
                          onOpenFile={openFile}
                          onFork={id ? (messageId) => void forkFrom(messageId) : undefined}
                          approvals={streaming && i === lastTurn ? view.waiting : undefined}
                          onDecide={(callId, allow) => void decide(callId, allow)}
                          busy={streaming}
                        />
                      )}
                    </div>
                  ))}
                  {error && <Alert variant="destructive">{error}</Alert>}
                </div>
              </div>
              <QuestionRail questions={turns.flatMap((t) => (t.question ? [t.question] : []))} active={readingQuestion} onJump={jumpTo} gutter={gutter} />
            </div>
            <div className="relative mx-auto w-full max-w-(--thread-max) px-4 pb-4 sm:px-6">
              {!atBottom && (
                <Button
                  variant="outline"
                  size="icon-sm"
                  className="absolute -top-12 left-1/2 -translate-x-1/2 animate-pop rounded-full shadow-md"
                  onClick={() => {
                    setAtBottom(true)
                    scroller.current?.scrollTo?.({ top: scroller.current.scrollHeight, behavior: 'smooth' })
                  }}
                  aria-label="Jump to the latest"
                >
                  <ArrowDown />
                </Button>
              )}
              <Composer streaming={streaming} onSend={send} onStop={() => abort.current?.abort()} uploads={uploads} model={model} tools={toolsPicker} autoFocus />
            </div>
          </>
        )}
      </div>
      {/* Beside the thread on a wide screen; over it, as a panel, on a narrow one. */}
      {filesOpen && wide && <div className="min-h-0 border-l">{panel}</div>}
      {!wide && (
        <Sheet open={filesOpen} onOpenChange={setFilesOpen}>
          <SheetContent side="right" className="gap-0 p-0">
            <SheetTitle className="sr-only">Files</SheetTitle>
            <SheetDescription className="sr-only">Files in this chat</SheetDescription>
            {panel}
          </SheetContent>
        </Sheet>
      )}
      {dragging && (
        <div className="pointer-events-none absolute inset-2 z-40 flex items-center justify-center rounded-2xl border-2 border-dashed border-primary bg-primary/5 backdrop-blur-[1px]">
          <p className="flex items-center gap-2 rounded-lg bg-popover px-4 py-2 font-medium shadow-md">
            <FileUp className="size-5 text-primary" aria-hidden="true" /> Drop files to attach them
          </p>
        </div>
      )}
    </div>
  )
}
