import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Archive, ArrowDown, Code2, FileUp, GitFork, Lightbulb, Lock, MessageSquare, Search, Sparkles, SquareTerminal } from 'lucide-react'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Link, useNavigate, useOutletContext, useParams, useSearchParams } from 'react-router'
import { PageSkeleton, QueryError } from '@/components/app/query-state'
import { Alert } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Sheet, SheetContent, SheetDescription, SheetTitle } from '@/components/ui/sheet'
import { toast } from '@/components/ui/toaster'
import { api, ApiError, errorMessage, infoQuery, type Me } from '@/lib/api'
import { useMedia } from '@/lib/use-media'
import { cn } from '@/lib/utils'
import { archiveChat, assistantQuery, cancelQueued, chatModel, configQuery, conversationQuery, forkChat, hurryChat, queueMessage, sendQueuedNow, stopChat, streamChat } from './api'
import { AssistantIcon } from './assistant-icon'
import { Composer } from './composer'
import { contextOf } from './context'
import { collectFiles } from './files'
import { FilesPanel } from './files-panel'
import { answerNews } from './format'
import { ChatHeader } from './header'
import { reduce, stopped, withQuestion, type LiveState } from './live'
import { QuestionRail } from './question-rail'
import { useOlderMessages } from './recall'
import { toolsOn } from './tools'
import { ToolsPicker } from './tools-picker'
import { ChatList } from './sidebar'
import { useListFolds } from './list-folds'
import { ChatTree, toTurns } from './tree'
import { AnswerTurn, CompactedMark, QuestionTurn } from './turns'
import { ArenaTurn, ComparePicker } from './arena'
import { arenaView, type CompareChoice } from './quality'
import type { Attachment, ChatConfig, ChatEvent, ChatSettings, Conversation, Message, QueuedMessage } from './types'
import { useUploads } from './uploads'
import { AssistantView } from './assistant-view'
import { ShareDialog } from './share-dialog'
import { chatToJson, chatToMarkdown, exportName, markdownToHtml } from './export'
import type { ExportKind } from './header'
import { saveBlob } from '@/lib/zip'
import { readsAloud, voiceQuery } from '@/lib/voice'
import type { Queued } from './composer'
import { speak, voicePrefix } from './sound'
import { TalkBar, TalkButton } from './talk'
import { useTalk, type TalkTurn } from './use-talk'
import { tellDesktop } from '@/lib/desktop'
import { TraceSheet } from '@/pages/admin/trace-view'
import { CanvasPanel } from './canvas-panel'
import { CanvasOpener, useCanvasState } from './canvas-context'

/** The id a question is shown under until the server gives it its own. */
let localCount = 0
const newLocalId = () => `local-${Date.now()}-${++localCount}`

export function ChatPage() {
  const { id, assistantId } = useParams()
  const navigate = useNavigate()
  const config = useQuery(configQuery)
  const [listOpen, setListOpen] = useState(false)
  const listFolded = useListFolds().listFolded
  // "Start a chat" from the gallery (/chat?assistant=…): a new chat with it; the address is tidied after.
  const [search, setSearch] = useSearchParams()
  useEffect(() => {
    if (search.has('assistant'))
      setSearch(
        (p) => {
          p.delete('assistant')
          return p
        },
        { replace: true },
      )
  }, [search, setSearch])
  // A new chat gets its id with its first message; the thread must survive that
  // navigation (it is mid-answer), so it is keyed by "which new chat" until then.
  const [fresh, setFresh] = useState(0)
  const [adopted, setAdopted] = useState<string | null>(null)
  // A new chat may start with an assistant (from its page, or the gallery).
  const [startIn, setStartIn] = useState<string | null>(() => (id ? null : search.get('assistant')))
  const threadKey = !id || id === adopted ? `new-${fresh}` : id
  const startNew = (assistant?: string) => {
    setFresh((n) => n + 1)
    setAdopted(null)
    setStartIn(assistant ?? null)
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
    <div className={cn('grid h-[calc(100dvh-3.5rem)] min-h-0', listFolded ? 'lg:grid-cols-[3rem_minmax(0,1fr)]' : 'lg:grid-cols-[16rem_minmax(0,1fr)] 2xl:grid-cols-[19rem_minmax(0,1fr)]')}>
      <div className="hidden min-h-0 border-r bg-sidebar lg:block">
        <ChatList activeId={id} activeAssistant={assistantId} onNew={() => startNew()} foldable />
      </div>
      <Sheet open={listOpen} onOpenChange={setListOpen}>
        <SheetContent side="left" className="w-80 gap-0 bg-sidebar p-0">
          <SheetTitle className="sr-only">Chats</SheetTitle>
          <SheetDescription className="sr-only">Your chats</SheetDescription>
          <ChatList activeId={id} activeAssistant={assistantId} onNew={() => startNew()} onNavigate={() => setListOpen(false)} />
        </SheetContent>
      </Sheet>
      {config.error ? (
        <div className="p-6">
          <QueryError error={config.error} retry={() => config.refetch()} />
        </div>
      ) : assistantId ? (
        <AssistantView key={assistantId} assistantId={assistantId} onOpenList={() => setListOpen(true)} onNewChat={() => startNew(assistantId)} />
      ) : config.data ? (
        <Thread key={threadKey} id={id} config={config.data} onAdopt={setAdopted} onOpenList={() => setListOpen(true)} startIn={id ? null : startIn} />
      ) : (
        <div className="p-6">
          <PageSkeleton />
        </div>
      )}
    </div>
  )
}

/** Why a stream was let go: the page left the chat (the answer goes on). */
const leaving = 'leaving'

const suggestions = [
  { icon: Code2, text: 'Write a Python script that renames photos by the date they were taken.' },
  { icon: Lightbulb, text: 'Explain the difference between a mutex and a semaphore, with a C example.' },
  { icon: Sparkles, text: 'Review this approach: caching API responses in Redis for five minutes.' },
]

function Thread({ id, config, onAdopt, onOpenList, startIn }: { id?: string; config: ChatConfig; onAdopt: (id: string) => void; onOpenList: () => void; startIn?: string | null }) {
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const loaded = useQuery({ ...conversationQuery(id ?? ''), enabled: !!id })
  const brand = useQuery(infoQuery).data?.name
  // Admins open an answer's trace (where its time went) from the answer.
  const me = useOutletContext<Me | undefined>()
  const [tracing, setTracing] = useState<string | null>(null)
  // A new chat started from an assistant's page is made with it.
  const [draft, setDraft] = useState<ChatSettings>(() => (startIn ? { assistantId: startIn } : {}))
  const [live, setLive] = useState<LiveState | null>(null)
  const liveRef = useRef<LiveState | null>(null)
  /** What to do once a run is over and saved (given where it ended): watch the queued message answered next. */
  const afterRun = useRef<(leaf: string | null | undefined) => void>(() => {})
  /** The page left the chat: nothing more is watched from here. */
  const gone = useRef(false)
  /** A question said in Talk while an answer ran: asked once it is over. */
  const talkNext = useRef<{ text: string; turn: TalkTurn } | null>(null)
  // Answers to what is said aloud are read aloud, unless the person (or the company) turned that off.
  const aloudToo = readsAloud(useQuery(voiceQuery).data)
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
  /** How a file that can run is shown, and whether the panel takes half the page (for a preview). */
  const [fileView, setFileView] = useState<'preview' | 'code'>('preview')
  const [panelWide, setPanelWide] = useState(false)
  // The canvas shares the Files panel's place: one shows at a time.
  const closeFiles = useCallback(() => setFilesOpen(false), [])
  const canvas = useCanvasState(id, wide, closeFiles)
  const canvasEvents = useRef(canvas.onEvent)
  useEffect(() => {
    canvasEvents.current = canvas.onEvent
  })
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
  // ↑ in the box: this chat's questions, newest first, then the person's in their other chats.
  const sentHere = useMemo(() => path.flatMap((m) => (m.role === 'user' ? [m.content] : [])).reverse(), [path])
  const older = useOlderMessages()
  const history = { here: sentHere, older }
  const files = useMemo(() => collectFiles(path, view.agents), [path, view.agents])
  const model = chatModel(config, settings.model)
  const toolsOnHere = toolsOn(config.tools, settings.tools)
  const title = live?.title ?? data?.title ?? null
  /** An answer is being written (not only a compaction the person asked for). */
  const answering = streaming && view.mode !== 'compact'
  // How full the context was at the last answer: its prompt and what it wrote.
  const lastUsage = [...path].reverse().find((m) => m.role === 'assistant' && m.promptTokens != null)
  // A compaction since the last answer: the gauge works out what the next request carries.
  const compactedAt = path.findLastIndex((m) => m.summary)
  const compacted = compactedAt >= 0 && (!lastUsage || compactedAt >= path.indexOf(lastUsage)) ? path[compactedAt]!.summary : null
  const context = contextOf(lastUsage, model?.context, settings.maxTokens ?? model?.maxOutput ?? null, compacted)

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

  /**
   * Streams one answer; false when the question never reached the server (it goes
   * back into the box). The answer runs on the server: leaving the page only stops
   * watching it. No body: watch the answer the chat is writing already.
   */
  /** Which run is current: a run that ended tidies up only while no newer one has started. */
  const runs = useRef(0)
  /** An answer was watched here already: the chat's own "answering" is not watched again. */
  const attached = useRef(false)
  const run = useCallback(
    async (conversationId: string, endpoint: string, body: object | null, start: LiveState, localId: string | null, watch?: (e: ChatEvent) => void): Promise<boolean> => {
      const me = ++runs.current
      attached.current = true
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
            watch?.(e)
            canvasEvents.current(e)
            setLive((s) => reduce(s ?? start, e, localId))
            // Done while the tab is hidden: the desktop says so (when the person turned that on).
            if (e.type === 'done' && endpoint !== 'compact') {
              const s = liveRef.current
              const saved = queryClient.getQueryData<Conversation>(conversationQuery(conversationId).queryKey)
              tellDesktop(`Answer ready: ${s?.title ?? saved?.title ?? 'Chat'}`, s?.messages.find((m) => m.id === s.current)?.content, `answer-${conversationId}`, () =>
                navigate(`/chat/${conversationId}`),
              )
            }
          },
          controller.signal,
        )
      } catch (err) {
        // The page left: the answer goes on without it, and nothing here is shown any more.
        if (controller.signal.reason === leaving) return true
        wasStopped = err instanceof DOMException && err.name === 'AbortError'
        if (!wasStopped)
          setError(err instanceof ApiError ? err.message : received ? 'The answer was interrupted.' : 'The message did not reach the server. It is back in the box below: send it again.')
      } finally {
        setStreaming(false)
        abort.current = null
        if (wasStopped) setLive((s) => (s ? stopped(s) : s))
        void queryClient.invalidateQueries({ queryKey: ['chat', 'list'] })
        // ↑ in another chat has this message among the person's others at once.
        void queryClient.invalidateQueries({ queryKey: ['chat', 'history'] })
        // A model refused as not loaded, or switched meanwhile: the menu learns of it now.
        void queryClient.invalidateQueries({ queryKey: configQuery.queryKey })
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
        if (runs.current === me) {
          setLive(null)
          afterRun.current(leaf)
        }
      }
      return received || wasStopped
    },
    [queryClient, navigate],
  )

  /** Stop: the server stops the answer and keeps what it has; this page stops watching at once. */
  const stop = async () => {
    const chat = streamingIn.current
    const watching = abort.current
    // Not answering any more (it ended as Stop was pressed): nothing to say.
    if (chat) await stopChat(chat).catch((e) => !(e instanceof ApiError && e.http === 409) && toast.error(errorMessage(e, 'The answer could not be stopped. It goes on.')))
    watching?.abort()
  }

  // Leaving the chat stops watching its answer, not the answer.
  useEffect(
    () => () => {
      gone.current = true
      abort.current?.abort(leaving)
    },
    [],
  )

  // A chat answering already (its page was closed, or another tab asked): watch the answer from its start.
  useEffect(() => {
    if (!id || !data?.answering || attached.current || abort.current) return
    attached.current = true
    void run(id, 'stream', null, { messages: data.messages, leaf: data.currentLeafId, notices: [], title: null, thinkingSince: null }, null)
  }, [id, data, run])

  /** Compacts the branch on screen: the model summarizes it, and the next answers read the summary instead. */
  const compact = async () => {
    if (!id || streaming) return
    const seen = { done: '', failed: '' }
    await run(id, 'compact', {}, { ...view, notices: [], mode: 'compact' }, null, (e) => {
      if (e.type === 'compacted') seen.done = `Compacted: ${e.covered} ${e.covered === 1 ? 'message' : 'messages'} summarized. The next answers read the summary.`
      if (e.type === 'notice') seen.done = e.text
      if (e.type === 'error') seen.failed = e.message
    })
    if (seen.failed) setError(seen.failed)
    else if (seen.done) toast.success(seen.done)
  }
  const canCompact = !!id && !streaming && path.length > 0 && !path.at(-1)!.id.startsWith('local-') && path.at(-1)!.role !== 'tool' && !path.at(-1)!.toolCalls?.length

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

  /** `talkTurn`: said in Talk, which hears the answer's events and its end, and reads it aloud. */
  const send = async (text: string, files?: Attachment[], talkTurn?: TalkTurn): Promise<boolean> => {
    const fromBox = files === undefined
    // Compare too: two models answer this message, side by side.
    const versus = fromBox ? compare : null
    if (versus) setCompare(null)
    // "/compact": a command, not a question.
    if (text === '/compact' && (files ?? uploads.attachments).length === 0) {
      if (!canCompact) {
        toast.error(id ? 'Compact when the answer is done.' : 'This chat has nothing to compact yet.')
        return false
      }
      await compact()
      return true
    }
    const conversationId = await ensureChat()
    if (!conversationId) {
      if (versus) setCompare(versus)
      talkTurn?.ended()
      return false
    }
    const parent = view.leaf
    const localId = newLocalId()
    const attachments = files ?? uploads.attachments
    // Asked by voice: answered aloud.
    const aloud = aloudToo && attachments.some((a) => a.fileName.startsWith(voicePrefix))
    // The files went with the question once the server has it: the box is free for the next one while the answer streams.
    const body = { content: text, attachments: attachments.map((a) => a.id), parentId: parent ?? undefined, root: parent === null, ...(versus?.models ? { models: versus.models } : {}), ...(talkTurn ? { spoken: true } : {}) }
    const sent = await run(conversationId, versus ? 'compare' : 'messages', body, withQuestion(view, localId, parent, text, attachments), localId, (e) => {
      if (e.type === 'question' && fromBox) uploads.clear()
      talkTurn?.watch(e)
      // Two answers to a comparison: neither is read aloud.
      if (e.type === 'done' && aloud && !versus) {
        const s = liveRef.current
        const said = s?.messages.find((m) => m.id === s.current)?.content
        if (said) speak(said).catch((err) => toast.error(errorMessage(err)))
      }
    })
    // Not sent: it goes back into the box, and Compare stays on for it.
    if (!sent && versus) setCompare(versus)
    talkTurn?.ended()
    return sent
  }

  // A new chat with an assistant: its name, look and conversation starters.
  const chosen = useQuery({ ...assistantQuery(draft.assistantId ?? ''), enabled: !!draft.assistantId && !id })
  const assistant = data ? data.assistant : chosen.data ? { ...chosen.data, noAccess: false } : null
  const move = async (assistantId: string | null) => {
    if (!id) return
    await api(`/api/chat/conversations/${id}`, { method: 'PATCH', body: { assistantId: assistantId ?? '00000000-0000-0000-0000-000000000000' } })
      .then(() => toast.success(assistantId ? 'Moved to the assistant' : 'Going on without the assistant', { description: 'The next answers read its instructions and files accordingly.' }))
      .catch((e) => toast.error(errorMessage(e)))
    await Promise.all([queryClient.invalidateQueries({ queryKey: ['chat'] }), queryClient.invalidateQueries({ queryKey: ['assistants'] })])
  }
  const [sharing, setSharing] = useState(false)
  // An assistant's conversation starters take the place of the suggestions.
  const starters = assistant?.starters.length ? assistant.starters.map((text) => ({ icon: MessageSquare, text })) : null

  /** The branch on screen as a file (made here, from what the page has), or its summary by the model. */
  const exportChat = (kind: ExportKind) => {
    const name = title ?? data?.title ?? 'Chat'
    const save = (text: string, type: string, file: string) => saveBlob(new Blob([text], { type }), file)
    if (kind === 'summary') {
      if (!id) return
      toast.promise(
        api<{ summary: string }>(`/api/chat/conversations/${id}/summary`).then((r) => save(r.summary, 'text/markdown', exportName(name, 'summary', 'md'))),
        { loading: 'Summarizing the chat…', success: 'Summary saved', error: (e) => errorMessage(e) },
      )
      return
    }
    if (kind === 'json') return save(chatToJson({ id: id ?? '', title: name }, path), 'application/json', exportName(name, '', 'json'))
    const md = chatToMarkdown(name, path)
    if (kind === 'md') return save(md, 'text/markdown', exportName(name, '', 'md'))
    const html = markdownToHtml(name, md)
    if (kind === 'html') return save(html, 'text/html', exportName(name, '', 'html'))
    // PDF: the page opens on its own, and the browser's print saves it as PDF.
    const url = URL.createObjectURL(new Blob([html], { type: 'text/html' }))
    const page = window.open(url, '_blank')
    if (!page) {
      toast.error('The browser blocked the new tab: allow pop-ups for this site, or export the web page and print it.')
      return
    }
    page.addEventListener('load', () => page.print(), { once: true })
    setTimeout(() => URL.revokeObjectURL(url), 60_000)
  }

  const [compare, setCompare] = useState<CompareChoice | null>(null)
  const comparePicker = <ComparePicker models={config.models} value={compare} onChange={setCompare} />

  // Written while an answer runs: each waits on the server (a reload, or another tab, still shows it)
  // and becomes the next question once the answer before is over, or at once with Send now (which
  // stops the answer). This page then watches that answer as it watched the one before.
  const queue: Queued[] = (data?.queued ?? []).map((q) => ({ key: q.id, text: q.content, attachments: q.attachments }))
  const setQueued = (chat: string, queued: QueuedMessage[]) =>
    queryClient.setQueryData<Conversation>(conversationQuery(chat).queryKey, (c) => (c ? { ...c, queued } : c))
  /** Watches the answer the chat is writing now (a queued message's): from its start, then live. */
  const watchNext = (chat: string, saved: Conversation) => {
    // Asked by voice: answered aloud, a queued voice message too.
    const asked = saved.messages.find((m) => m.id === saved.currentLeafId)
    const aloud = aloudToo && asked?.role === 'user' && asked.attachments.some((a) => a.fileName.startsWith(voicePrefix))
    void run(chat, 'stream', null, { messages: saved.messages, leaf: saved.currentLeafId, notices: [], title: null, thinkingSince: null }, null, (e) => {
      if (e.type !== 'done' || !aloud) return
      const s = liveRef.current
      const said = s?.messages.find((m) => m.id === s.current)?.content
      if (said) speak(said).catch((err) => toast.error(errorMessage(err)))
    })
  }
  const enqueue = async (text: string): Promise<boolean> => {
    const chat = streamingIn.current ?? id
    if (!chat) return false
    if (text === '/compact' && uploads.attachments.length === 0) {
      toast.error('Compact when the answer is done.')
      return false
    }
    try {
      const r = await queueMessage(chat, { content: text, attachments: uploads.attachments.map((a) => a.id) })
      uploads.clear()
      setQueued(chat, r.queued)
      // The answer ended meanwhile: it went at once.
      if (r.answering && !abort.current) await refreshThenWatch(chat)
      return true
    } catch (e) {
      toast.error(errorMessage(e, 'The message could not be queued. It is back in the box.'))
      return false
    }
  }
  const sendNow = async (key: string) => {
    const chat = streamingIn.current ?? id
    if (!chat) return
    try {
      // The server stops the answer; when its stream ends, this page watches the one that follows.
      const r = await sendQueuedNow(chat, key)
      setQueued(chat, r.queued)
      if (r.answering && !abort.current) await refreshThenWatch(chat)
    } catch (e) {
      toast.error(errorMessage(e))
      void queryClient.invalidateQueries({ queryKey: conversationQuery(chat).queryKey })
    }
  }
  const unqueue = async (key: string) => {
    const chat = streamingIn.current ?? id
    if (!chat) return
    setQueued(chat, (data?.queued ?? []).filter((q) => q.id !== key))
    await cancelQueued(chat, key).catch((e) => toast.error(errorMessage(e)))
    void queryClient.invalidateQueries({ queryKey: conversationQuery(chat).queryKey })
  }
  /** The chat as the server has it now, then its answer watched when it is writing one. */
  const refreshThenWatch = async (chat: string) => {
    await queryClient.invalidateQueries({ queryKey: conversationQuery(chat).queryKey })
    const saved = queryClient.getQueryData<Conversation>(conversationQuery(chat).queryKey)
    if (saved?.answering && !abort.current) watchNext(chat, saved)
  }
  // Called by an answer's run once it is over and saved: a queued message is the chat's new question by then,
  // answering. Only a new one: the same answer still listed as answering is not watched again.
  useEffect(() => {
    afterRun.current = (leaf) => {
      // A question said in Talk while the answer ran goes now, as spoken.
      const spoken = talkNext.current
      if (spoken) {
        talkNext.current = null
        void send(spoken.text, [], spoken.turn)
        return
      }
      const chat = streamingIn.current ?? id
      const saved = chat ? queryClient.getQueryData<Conversation>(conversationQuery(chat).queryKey) : undefined
      if (chat && saved?.answering && saved.currentLeafId !== leaf && !abort.current && !gone.current) watchNext(chat, saved)
    }
  })

  // Talk: what is said is asked as a spoken question (after the answer running, if one is); speaking over an answer stops it.
  const talk = useTalk({
    onHeard: (text, turn) => {
      if (streaming) {
        // Said again before the answer ended: the newer words are the question.
        talkNext.current?.turn.ended()
        talkNext.current = { text, turn }
      } else void send(text, [], turn)
    },
    onInterrupt: () => void stop(),
    readAloud: aloudToo,
  })
  const talkButton = <TalkButton state={talk.state} onStart={() => void talk.start()} onEnd={talk.end} />

  const edit = (m: Message, text: string) => {
    if (!id) return
    const localId = newLocalId()
    void run(id, 'messages', { content: text, attachments: m.attachments.map((a) => a.id), parentId: m.parentId ?? undefined, root: m.parentId === null }, withQuestion(view, localId, m.parentId, text, m.attachments), localId)
  }

  const regenerate = (question: Message, overrides?: { model?: string; thinking?: string; length?: 'shorter' | 'longer'; answerId?: string }) => {
    if (!id) return
    void run(id, 'regenerate', { messageId: question.id, ...overrides }, { ...view, leaf: question.id, notices: [] }, null)
  }

  const switchTo = async (messageId: string) => {
    if (!id || streaming) return
    const leaf = tree.leafBelow(messageId)
    queryClient.setQueryData<Conversation>(conversationQuery(id).queryKey, (c) => (c ? { ...c, currentLeafId: leaf } : c))
    await api(`/api/chat/conversations/${id}/leaf`, { method: 'PUT', body: { messageId } }).catch((e) => toast.error(errorMessage(e)))
  }

  // Opened at a message (a search result): onto its branch, then to it, marked for a moment.
  const [params, setParams] = useSearchParams()
  const switchingTo = useRef<string | null>(null)
  const at = params.get('at')
  useEffect(() => {
    if (!at || !data || streaming) return
    const m = data.messages.find((x) => x.id === at)
    const target = m?.role === 'tool' ? m.parentId : m?.id
    if (!target) return
    if (!path.some((x) => x.id === target)) {
      if (switchingTo.current !== target) {
        switchingTo.current = target
        void switchTo(target)
      }
      return
    }
    const el = document.querySelector<HTMLElement>(`[data-question="${target}"], [data-message="${target}"]`)
    if (!el) return
    el.scrollIntoView({ block: 'center' })
    el.classList.add('found')
    setTimeout(() => el.classList.remove('found'), 2400)
    switchingTo.current = null
    setParams(
      (p) => {
        p.delete('at')
        return p
      },
      { replace: true },
    )
  })

  const openFile = (name: string) => {
    const f = [...files].reverse().find((x) => x.name === name)
    setSelectedFile(f?.key ?? null)
    setFileView('preview')
    setFilesOpen(true)
    canvas.setOpen(false)
  }

  const openPreview = (code: string) => {
    const f = [...files].reverse().find((x) => x.kind === 'code' && x.code === code)
    setSelectedFile(f?.key ?? null)
    setFileView('preview')
    setPanelWide(true)
    setFilesOpen(true)
    canvas.setOpen(false)
  }

  if (id && loaded.isPending) return <div className="p-6"><PageSkeleton /></div>
  if (id && loaded.error) return <div className="p-6"><QueryError error={loaded.error} retry={() => loaded.refetch()} /></div>

  const empty = turns.length === 0
  const lastTurn = turns.length - 1
  const panel = (
    <FilesPanel
      files={files}
      title={title}
      selected={selectedFile}
      onSelect={(key) => {
        setSelectedFile(key)
        setFileView('preview')
      }}
      onClose={() => setFilesOpen(false)}
      view={fileView}
      onView={setFileView}
      wide={wide ? panelWide : undefined}
      onWide={setPanelWide}
    />
  )
  const canvasOpen = canvas.open && !!id
  const sideOpen = filesOpen || canvasOpen
  const sideWide = canvasOpen ? canvas.wide : panelWide
  const side = canvasOpen ? (
    <CanvasPanel
      chatId={id}
      selected={canvas.selected}
      onSelect={canvas.setSelected}
      onClose={() => canvas.setOpen(false)}
      wide={wide ? canvas.wide : undefined}
      onWide={canvas.setWide}
      onSend={(text) => send(text, [])}
      busy={streaming}
    />
  ) : (
    panel
  )

  return (
    <CanvasOpener value={canvas.reveal}>
      <div
        className={cn(
          'relative grid min-h-0 min-w-0',
          sideOpen && wide && (sideWide ? 'grid-cols-[minmax(0,1fr)_minmax(0,1fr)]' : 'grid-cols-[minmax(0,1fr)_26rem] 2xl:grid-cols-[minmax(0,1fr)_34rem]'),
        )}
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
            onChange={change}
            filesCount={files.length}
            filesOpen={filesOpen && !canvasOpen}
            onToggleFiles={() => {
              setFilesOpen(!filesOpen || canvasOpen)
              canvas.setOpen(false)
            }}
            canvas={id ? { count: canvas.count, open: canvasOpen, onToggle: () => (canvasOpen ? canvas.setOpen(false) : canvas.reveal(canvas.selected)) } : undefined}
            onOpenList={onOpenList}
            chat={id && data ? { id, title: title ?? data.title, archived: !!data.archivedAt } : undefined}
            onExport={id && path.length ? exportChat : undefined}
            assistant={assistant}
            onMove={id ? (assistantId) => void move(assistantId) : undefined}
            onShare={id && path.length ? () => setSharing(true) : undefined}
            onCompact={canCompact ? () => void compact() : undefined}
          />
          {empty ? (
            <div className="flex min-h-0 flex-1 flex-col items-center justify-center overflow-y-auto px-4 py-8">
              <div className="w-full max-w-2xl animate-enter">
                {assistant ? (
                  <div className="mb-6 grid justify-items-center gap-2 text-center">
                    <AssistantIcon icon={assistant.icon} color={assistant.color} size="lg" />
                    <h1 dir="auto" className="text-2xl font-semibold tracking-tight">
                      {assistant.name}
                    </h1>
                    {chosen.data?.description && (
                      <p dir="auto" className="text-muted-foreground">
                        {chosen.data.description}
                      </p>
                    )}
                  </div>
                ) : (
                  <>
                    <h1 className="mb-2 text-center text-2xl font-semibold tracking-tight">What can I help with?</h1>
                    <p className="mb-6 text-center text-muted-foreground">
                      {toolsOnHere.includes('argus') ? 'Ask anything. Argus searches the code you have access to in GitLab.' : 'Ask anything, or attach documents, spreadsheets, slides, PDFs, code and images to ask about them.'}
                    </p>
                  </>
                )}
                {error && (
                  <Alert variant="destructive" className="mb-3">
                    {error}
                  </Alert>
                )}
                <TalkBar state={talk.state} onEnd={talk.end} />
                <Composer streaming={streaming} onSend={send} onStop={() => void stop()} uploads={uploads} model={model} tools={toolsPicker} compare={comparePicker} talk={talkButton} history={history} autoFocus big />
                <div className={cn('stagger mt-4 grid gap-2', starters ? 'sm:grid-cols-2' : 'sm:grid-cols-3')} aria-label={starters ? 'Conversation starters' : undefined}>
                  {(starters ?? (config.argus ? [{ icon: Search, text: 'Which of our repositories call the payment service, and where?' }, ...suggestions.slice(0, 2)] : suggestions)).map((s) => (
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
                <output className="sr-only">{answerNews(answering, path)}</output>
                <div ref={thread} onScroll={onScroll} className="relative min-h-0 flex-1 overflow-y-auto">
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
                    {data?.assistant?.noAccess && (
                      <output className="flex flex-wrap items-center gap-2 rounded-lg border border-warning/40 bg-warning/10 px-3 py-2 text-sm">
                        <Lock className="size-4 text-warning-ink" aria-hidden="true" />
                        <span className="flex-1">
                          You no longer have access to the assistant <bdi className="font-medium">{data.assistant.name}</bdi>: this chat cannot go on with it.
                        </span>
                        <Button variant="outline" size="sm" className="h-7" onClick={() => void move(null)}>
                          Go on without it
                        </Button>
                      </output>
                    )}
                    {data?.origin === 'code-arena' && (
                      <p className="-mb-2 flex min-w-0 items-center gap-1.5 text-xs text-muted-foreground">
                        <SquareTerminal className="size-3.5 shrink-0" aria-hidden="true" />
                        <span className="min-w-0">
                          A Code Arena session{data.originPlace ? <> in <code className="font-mono">{data.originPlace}</code></> : null}, kept in step with it both ways. Its files and commands are on that
                          machine: here the chat's own tools answer.
                        </span>
                      </p>
                    )}
                    {data?.forkedFrom && (
                      <p className="-mb-2 flex min-w-0 items-center gap-1.5 text-xs text-muted-foreground">
                        <GitFork className="size-3.5 shrink-0" aria-hidden="true" /> Forked from
                        <Link to={`/chat/${data.forkedFrom.id}`} className="truncate font-medium text-foreground underline-offset-2 hover:underline">
                          {data.forkedFrom.title}
                        </Link>
                      </p>
                    )}
                    {turns.map((t, i) => {
                      const summary = [t.question, ...t.answer].find((m) => m?.summary)?.summary
                      // A question compared (arena mode): both answers side by side.
                      const arena = t.question ? arenaView(t.question, data?.arenas, view.arena, answering && i === lastTurn) : null
                      return (
                        <div key={t.question?.id ?? t.answer[0]?.id ?? i} className="grid gap-4">
                          {t.question && <QuestionTurn m={t.question} siblings={tree.siblings(t.question)} busy={streaming} onSwitch={switchTo} onEdit={edit} />}
                          {arena && t.question ? (
                            <ArenaTurn
                              arena={arena}
                              tree={tree}
                              question={t.question}
                              config={config}
                              chatId={id}
                              thinkingSince={answering && i === lastTurn ? view.thinkingSince : null}
                              queued={answering && i === lastTurn ? view.queued : null}
                              busy={streaming}
                              onOpenFile={openFile}
                              onPreview={openPreview}
                              approvals={answering && i === lastTurn ? view.waiting : undefined}
                              onDecide={(callId, allow) => void decide(callId, allow)}
                              calls={answering && i === lastTurn ? view.calls : undefined}
                              agents={i === lastTurn ? view.agents : undefined}
                              onHurry={answering && i === lastTurn && id ? () => void hurryChat(id).catch((e) => toast.error(errorMessage(e))) : undefined}
                            />
                          ) : (t.answer.length > 0 || (answering && i === lastTurn)) && (
                            <AnswerTurn
                              answer={t.answer}
                              siblings={t.answer[0] ? tree.siblings(t.answer[0]) : []}
                              live={answering && i === lastTurn}
                              thinkingSince={answering && i === lastTurn ? view.thinkingSince : null}
                              queued={answering && i === lastTurn ? view.queued : null}
                              compacting={answering && i === lastTurn && !!view.compacting}
                              notices={i === lastTurn ? view.notices : []}
                              config={config}
                              question={t.question}
                              onSwitch={switchTo}
                              onRegenerate={regenerate}
                              onOpenFile={openFile}
                              onPreview={openPreview}
                              onFork={id ? (messageId) => void forkFrom(messageId) : undefined}
                              approvals={answering && i === lastTurn ? view.waiting : undefined}
                              calls={answering && i === lastTurn ? view.calls : undefined}
                              agents={i === lastTurn ? view.agents : undefined}
                              onAnswer={!streaming && i === lastTurn ? send : undefined}
                              onHurry={answering && i === lastTurn && id ? () => void hurryChat(id).catch((e) => toast.error(errorMessage(e))) : undefined}
                              onDecide={(callId, allow) => void decide(callId, allow)}
                              research={answering && i === lastTurn && !!view.research}
                              onTrace={me?.isAdmin ? setTracing : undefined}
                              busy={streaming}
                              feedbackIn={id}
                            />
                          )}
                          {summary && <CompactedMark summary={summary} onOpenFile={openFile} />}
                        </div>
                      )
                    })}
                    {streaming && view.mode === 'compact' && (
                      <output className="flex items-center gap-2 text-sm text-muted-foreground">
                        <span className="flex gap-1" aria-hidden="true">
                          <span className="size-1.5 animate-bounce rounded-full bg-current [animation-delay:-0.3s]" />
                          <span className="size-1.5 animate-bounce rounded-full bg-current [animation-delay:-0.15s]" />
                          <span className="size-1.5 animate-bounce rounded-full bg-current" />
                        </span>
                        Compacting the chat: summarizing it so the next answers read the summary…
                      </output>
                    )}
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
                <TalkBar state={talk.state} onEnd={talk.end} />
                <Composer
                  streaming={streaming}
                  onSend={send}
                  talk={talkButton}
                  queued={queue}
                  onQueue={enqueue}
                  onSendNow={(key) => void sendNow(key)}
                  onUnqueue={(key) => void unqueue(key)}
                 
                  compare={comparePicker}
                  onStop={() => void stop()}
                  uploads={uploads}
                  model={model}
                  tools={toolsPicker}
                  context={context}
                  onCompact={canCompact ? () => void compact() : undefined}
                  history={history}
                  autoFocus
                />
              </div>
            </>
          )}
        </div>
        {/* Beside the thread on a wide screen; over it, as a panel, on a narrow one. */}
        {sideOpen && wide && <div className="min-h-0 border-l">{side}</div>}
        {!wide && (
          <Sheet
            open={sideOpen}
            onOpenChange={(open) => {
              if (open) return
              setFilesOpen(false)
              canvas.setOpen(false)
            }}
          >
            <SheetContent side="right" hideClose className={cn('gap-0 p-0', (canvasOpen || fileView === 'preview') && 'sm:max-w-3xl')}>
              <SheetTitle className="sr-only">{canvasOpen ? 'Canvas' : 'Files'}</SheetTitle>
              <SheetDescription className="sr-only">{canvasOpen ? 'Documents and code of this chat' : 'Files in this chat'}</SheetDescription>
              {side}
            </SheetContent>
          </Sheet>
        )}
        {me?.isAdmin && <TraceSheet answerId={tracing} onClose={() => setTracing(null)} />}
        {id && <ShareDialog chatId={id} leafId={view.leaf} open={sharing} onOpenChange={setSharing} />}
        {dragging && (
          <div className="pointer-events-none absolute inset-2 z-40 flex items-center justify-center rounded-2xl border-2 border-dashed border-primary bg-primary/5 backdrop-blur-[1px]">
            <p className="flex items-center gap-2 rounded-lg bg-popover px-4 py-2 font-medium shadow-md">
              <FileUp className="size-5 text-primary" aria-hidden="true" /> Drop files to attach them
            </p>
          </div>
        )}
      </div>
    </CanvasOpener>
  )
}
