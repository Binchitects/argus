import { useInfiniteQuery, useQuery } from '@tanstack/react-query'
import { ArrowLeft, Download, ExternalLink, FileArchive, FileCode2, FileDown, FileSearch, FileText, Image as ImageIcon, Maximize2, Minimize2, Play, X, ZoomIn, ZoomOut } from 'lucide-react'
import { useEffect, useRef, useState } from 'react'
import { Button } from '@/components/ui/button'
import { EmptyState } from '@/components/ui/empty-state'
import { Skeleton } from '@/components/ui/skeleton'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { toast } from '@/components/ui/toaster'
import { Tooltip } from '@/components/ui/tooltip'
import { api, errorMessage } from '@/lib/api'
import { formatValue } from '@/lib/format'
import { saveBlob } from '@/lib/zip'
import { cn } from '@/lib/utils'
import { previewKindOf, type PreviewKind } from '@/preview/kind'
import { attachmentUrl, configQuery, downloadUrl } from './api'
import { gitlabLink } from './argus'
import { CodeBlock } from './code-block'
import type { FileItem } from './files'
import type { Attachment } from './types'
import { parseFence } from './files'
import { zipFiles, zipName } from './files-zip'
import { ImageViewer } from './image-viewer'
import { asViewerImages } from './viewer-images'
import { LivePreview } from './live-preview'
import { MediaPlayer } from './media'
import { isMedia } from './sound'

/**
 * Like Claude's: every file in the branch on screen, and a viewer for the one
 * chosen. Code that can run (a page, a picture, a diagram, a component) shows
 * live or as code; `wide` (on a wide screen) gives the panel half the page.
 */
export function FilesPanel({
  files,
  selected,
  onSelect,
  onClose,
  view = 'preview',
  onView,
  wide,
  onWide,
  title,
}: {
  files: FileItem[]
  /** The chat's title: the zip of all files is named after it. */
  title?: string | null
  selected: string | null
  onSelect: (key: string | null) => void
  onClose: () => void
  view?: 'preview' | 'code'
  onView?: (view: 'preview' | 'code') => void
  wide?: boolean
  onWide?: (wide: boolean) => void
}) {
  const current = files.find((f) => f.key === selected) ?? null
  const kind = current ? previewOf(current) : null
  const previewing = kind !== null && view === 'preview'
  const [zipping, setZipping] = useState(false)
  const downloadAll = async () => {
    setZipping(true)
    try {
      const { zip, missing } = await zipFiles(files)
      saveBlob(zip, zipName(title ?? null))
      if (missing.length) toast.warning(`Left out: ${missing.join(', ')}`, { description: 'They could not be had (removed, or the connection dropped).' })
    } catch (e) {
      toast.error(errorMessage(e, 'The files could not be zipped.'))
    } finally {
      setZipping(false)
    }
  }
  return (
    <aside aria-label="Files" className="flex h-full min-h-0 flex-col bg-card">
      <header className="flex h-12 shrink-0 items-center gap-2 border-b px-3">
        {current ? (
          <Button variant="ghost" size="sm" className="-ml-1 min-w-0" onClick={() => onSelect(null)} aria-label="All files">
            <ArrowLeft /> <span className="truncate">{current.name}</span>
          </Button>
        ) : (
          <h2 className="text-sm font-semibold">Files ({files.length})</h2>
        )}
        <span className="ml-auto flex shrink-0 items-center gap-1">
          {kind && (
            <Tabs value={view} onValueChange={(v) => onView?.(v as 'preview' | 'code')}>
              <TabsList className="h-8">
                <TabsTrigger value="preview" className="px-2 text-xs">
                  Preview
                </TabsTrigger>
                <TabsTrigger value="code" className="px-2 text-xs">
                  Code
                </TabsTrigger>
              </TabsList>
            </Tabs>
          )}
          {!current && files.length > 0 && (
            <Tooltip content="Download all as a zip">
              <Button variant="ghost" size="icon-sm" onClick={downloadAll} loading={zipping} aria-label={`Download all ${files.length} files as a zip`}>
                <FileArchive />
              </Button>
            </Tooltip>
          )}
          {wide !== undefined && onWide && (
            <Tooltip content={wide ? 'Narrow the panel' : 'Widen the panel'}>
              <Button variant="ghost" size="icon-sm" onClick={() => onWide(!wide)} aria-label={wide ? 'Narrow the panel' : 'Widen the panel'} aria-pressed={wide}>
                {wide ? <Minimize2 /> : <Maximize2 />}
              </Button>
            </Tooltip>
          )}
          <Button variant="ghost" size="icon-sm" onClick={onClose} aria-label="Close files">
            <X />
          </Button>
        </span>
      </header>
      <div className={cn('min-h-0 flex-1 p-3', previewing ? 'flex flex-col' : 'overflow-y-auto')}>
        {previewing && current ? (
          current.kind === 'attachment' ? (
            <AttachmentPreview kind={kind} attachment={current.attachment} />
          ) : (
            <LivePreview kind={kind} code={current.code} name={current.name} />
          )
        ) : current ? (
          <Viewer file={current} />
        ) : files.length === 0 ? (
          <EmptyState icon={FileCode2} title="No files yet" className="border-0">
            Attachments, files Argus reads, and code the model writes show up here.
          </EmptyState>
        ) : (
          <ul className="grid gap-1">
            {files.map((f) => (
              <li key={f.key}>
                <button
                  type="button"
                  onClick={() => onSelect(f.key)}
                  className="flex w-full items-center gap-3 rounded-lg px-2 py-2 text-left outline-none hover:bg-accent focus-visible:ring-[3px] focus-visible:ring-ring"
                >
                  <span className={cn('flex size-8 shrink-0 items-center justify-center rounded-md', f.kind === 'attachment' ? 'bg-muted text-muted-foreground' : 'bg-primary/10 text-primary-ink')}>
                    {previewOf(f) ? <Play className="size-4" /> : f.kind === 'code' ? <FileCode2 className="size-4" /> : f.kind === 'repo' ? <FileSearch className="size-4" /> : f.attachment.kind === 'image' ? <ImageIcon className="size-4" /> : f.attachment.kind === 'file' ? <FileDown className="size-4" /> : <FileText className="size-4" />}
                  </span>
                  <span className="grid min-w-0">
                    <span className="truncate text-sm font-medium">{f.name}</span>
                    <span className="truncate text-xs text-muted-foreground">
                      {f.kind === 'code'
                        ? `${f.code.split('\n').length} lines · written in this chat`
                        : f.kind === 'repo'
                          ? `read by Argus${f.repo ? ` from ${f.repo}` : ''}`
                          : `${formatValue(f.attachment.size, 'bytes')} · ${f.made ? 'made in this chat' : 'attached'}`}
                    </span>
                  </span>
                </button>
              </li>
            ))}
          </ul>
        )}
      </div>
    </aside>
  )
}

/** What a file can be previewed as: code by its fence, a text file (a page Python wrote, one attached) by its name. */
function previewOf(f: FileItem): PreviewKind | null {
  if (f.kind === 'code') return f.preview
  if (f.kind === 'attachment' && f.attachment.kind === 'text') return previewKindOf(null, f.attachment.fileName, '')
  return null
}

/** A file shown running: its own bytes when kept (a whole page, past what the model reads), else its text. */
function AttachmentPreview({ kind, attachment }: { kind: PreviewKind; attachment: Extract<FileItem, { kind: 'attachment' }>['attachment'] }) {
  const text = useAttachmentText(attachment.id, attachment.fileName, attachment.original === true)
  if (text.isPending) return <Skeleton className="h-64" />
  if (text.error) return <p className="text-sm text-destructive-ink">{text.error.message}</p>
  return <LivePreview kind={kind} code={text.data} name={attachment.fileName} />
}

function useAttachmentText(id: string, name: string, original: boolean) {
  return useQuery({
    queryKey: ['chat', 'attachment', id, original ? 'original' : 'text'],
    queryFn: async ({ signal }) => {
      const res = await fetch(original ? downloadUrl(id) : attachmentUrl(id), { signal, credentials: 'same-origin', headers: { 'X-Requested-With': 'fetch' } })
      if (!res.ok) throw new Error(`Could not open ${name} (HTTP ${res.status}).`)
      return res.text()
    },
    staleTime: Infinity,
  })
}

/** A document with pages to show as pictures (its original kept): PDF, Word, PowerPoint, Excel, OpenDocument. */
const paged = /\.(pdf|docx?|odt|rtf|pptx?|odp|xlsx?|ods)$/i
const hasPages = (a: Attachment) => a.original === true && paged.test(a.fileName)

function Viewer({ file }: { file: FileItem }) {
  const [view, setView] = useState<'pages' | 'text'>('pages')
  if (file.kind === 'code') return <CodeBlock code={file.code} lang={file.lang} name={file.name} />
  if (file.kind === 'repo') return <RepoViewer file={file} />
  if (file.attachment.kind === 'image') return <ImageFile attachment={file.attachment} />
  if (isMedia(file.attachment)) return <MediaPlayer a={file.attachment} className={file.attachment.kind === 'video' ? 'max-h-[70vh] w-full rounded-lg border bg-black' : undefined} />
  if (hasPages(file.attachment)) {
    if (file.attachment.kind !== 'text') return <DocumentPages attachment={file.attachment} />
    // Its pages as they look, or the text the model read.
    return (
      <Tabs value={view} onValueChange={(v) => setView(v as 'pages' | 'text')} className="gap-3">
        <TabsList className="h-8">
          <TabsTrigger value="pages" className="px-2 text-xs">
            Pages
          </TabsTrigger>
          <TabsTrigger value="text" className="px-2 text-xs">
            Text
          </TabsTrigger>
        </TabsList>
        <TabsContent value="pages">
          <DocumentPages attachment={file.attachment} />
        </TabsContent>
        <TabsContent value="text">
          <TextViewer id={file.attachment.id} name={file.name} truncated={file.attachment.truncated} />
        </TabsContent>
      </Tabs>
    )
  }
  if (file.attachment.kind === 'file') return <DownloadOnly attachment={file.attachment} />
  return (
    <div className="grid gap-2">
      {file.attachment.original && (
        <a href={downloadUrl(file.attachment.id)} download={file.attachment.fileName} className="flex w-fit items-center gap-1.5 text-xs font-medium text-primary-ink underline-offset-2 hover:underline">
          <Download className="size-3.5" aria-hidden="true" /> Download {file.attachment.fileName}
        </a>
      )}
      <TextViewer id={file.attachment.id} name={file.name} truncated={file.attachment.truncated} />
    </div>
  )
}

/** Pages the server draws at a time: the first on first look, then the next as the reader reaches the end. */
const pageBatch = 20

interface PagesState {
  total: number
  drawn: number
  pages: string[]
}

/**
 * A document's pages, drawn as pictures on first look (in the sandbox), one under
 * the other: zoomed in the panel with the buttons, or opened one by one full size.
 * A long one shows its first twenty; the next twenty are drawn as the reader
 * reaches the end (or asks with the button), to the last page.
 */
function DocumentPages({ attachment }: { attachment: Attachment }) {
  const pages = useInfiniteQuery({
    queryKey: ['chat', 'pages', attachment.id],
    initialPageParam: pageBatch,
    queryFn: ({ pageParam, signal }) => api<PagesState>(`/api/chat/attachments/${attachment.id}/pages?upTo=${pageParam}`, { signal }),
    getNextPageParam: (last) => (last.drawn < last.total ? last.drawn + pageBatch : undefined),
    staleTime: Infinity,
    retry: false,
  })
  const [width, setWidth] = useState(100)
  const [viewing, setViewing] = useState<number | null>(null)
  const { hasNextPage, isFetchingNextPage, isFetchNextPageError, fetchNextPage } = pages
  // Reaching the end of the pages drawn asks for the next ones (not again after a failure: the button retries).
  const end = useRef<HTMLLIElement>(null)
  useEffect(() => {
    const el = end.current
    if (!el || !hasNextPage || isFetchingNextPage || isFetchNextPageError || typeof IntersectionObserver === 'undefined') return
    const seen = new IntersectionObserver((entries) => entries.some((e) => e.isIntersecting) && void fetchNextPage(), { rootMargin: '600px 0px' })
    seen.observe(el)
    return () => seen.disconnect()
  }, [hasNextPage, isFetchingNextPage, isFetchNextPageError, fetchNextPage])
  if (pages.isPending) {
    return (
      <div className="grid gap-2" aria-busy="true">
        <p className="text-xs text-muted-foreground">Drawing its pages…</p>
        <Skeleton className="aspect-[1/1.414] w-full" />
      </div>
    )
  }
  if (pages.isError && !pages.data) {
    return (
      <div className="grid gap-2">
        <p className="text-sm text-destructive-ink">{errorMessage(pages.error)}</p>
        <a href={downloadUrl(attachment.id)} download={attachment.fileName} className="flex w-fit items-center gap-1.5 text-xs font-medium text-primary-ink underline-offset-2 hover:underline">
          <Download className="size-3.5" aria-hidden="true" /> Download {attachment.fileName}
        </a>
      </div>
    )
  }
  // Each answer lists every page drawn so far: the last is the whole of it.
  const { total, drawn, pages: drawnPages } = pages.data.pages.at(-1)!
  const nextTo = Math.min(total, drawn + pageBatch)
  const images = drawnPages.map((src, i) => ({
    key: src, src, name: attachment.fileName, detail: `Page ${i + 1} of ${total}`, download: { href: downloadUrl(attachment.id), name: attachment.fileName },
  }))
  return (
    <div className="grid gap-2">
      <div className="flex flex-wrap items-center gap-1">
        <p className="min-w-0 flex-1 text-xs text-muted-foreground">
          {total} page{total === 1 ? '' : 's'}
          {drawn < total && ` · the first ${drawn} shown`}
        </p>
        <Button variant="ghost" size="icon-sm" onClick={() => setWidth((w) => Math.max(50, w - 25))} disabled={width <= 50} aria-label="Zoom out">
          <ZoomOut />
        </Button>
        <Button variant="ghost" size="sm" className="h-8 w-14 px-1 text-xs tabular-nums" onClick={() => setWidth(100)} aria-label={`Zoom ${width}%: fit the panel`}>
          {width}%
        </Button>
        <Button variant="ghost" size="icon-sm" onClick={() => setWidth((w) => Math.min(300, w + 25))} disabled={width >= 300} aria-label="Zoom in">
          <ZoomIn />
        </Button>
        <a href={downloadUrl(attachment.id)} download={attachment.fileName} className="ml-1 flex items-center gap-1 text-xs font-medium text-primary-ink underline-offset-2 hover:underline">
          <Download className="size-3.5" aria-hidden="true" /> Download
        </a>
      </div>
      <ol className="grid gap-3 overflow-x-auto" aria-label={`Pages of ${attachment.fileName}`}>
        {images.map((p, i) => (
          <li key={p.key} ref={i === images.length - 1 ? end : undefined} style={{ width: `${width}%` }} className="mx-auto max-w-none">
            <button type="button" onClick={() => setViewing(i)} className="block w-full cursor-zoom-in rounded-md outline-none focus-visible:ring-[3px] focus-visible:ring-ring" aria-label={`Page ${i + 1}: view full size`}>
              <img src={p.src} alt={`Page ${i + 1} of ${total}`} loading="lazy" className="w-full rounded-md border bg-white shadow-sm" />
            </button>
          </li>
        ))}
      </ol>
      {drawn < total && (
        <div className="grid justify-items-center gap-1.5 py-1 text-center">
          {isFetchingNextPage ? (
            <p className="text-xs text-muted-foreground" aria-live="polite">
              Drawing pages {drawn + 1} to {nextTo}…
            </p>
          ) : (
            <>
              {isFetchNextPageError && <p className="text-xs text-destructive-ink">{errorMessage(pages.error)}</p>}
              <Button variant="outline" size="sm" onClick={() => void fetchNextPage()}>
                {isFetchNextPageError ? 'Try again: pages' : 'Show pages'} {drawn + 1} to {nextTo}
              </Button>
            </>
          )}
        </div>
      )}
      <ImageViewer images={images} index={viewing} onIndex={setViewing} />
    </div>
  )
}

/** A file that is neither text nor a picture (a .zip Python wrote): what it is, and the file itself. */
function DownloadOnly({ attachment }: { attachment: Extract<FileItem, { kind: 'attachment' }>['attachment'] }) {
  return (
    <div className="grid justify-items-center gap-3 rounded-lg border border-dashed px-4 py-8 text-center">
      <FileDown className="size-8 text-muted-foreground" aria-hidden="true" />
      <div>
        <p className="font-medium break-all">{attachment.fileName}</p>
        <p className="text-sm text-muted-foreground">{formatValue(attachment.size, 'bytes')} · not a file the page can show</p>
      </div>
      <Button asChild size="sm">
        <a href={downloadUrl(attachment.id)} download={attachment.fileName}>
          <Download /> Download
        </a>
      </Button>
    </div>
  )
}

function ImageFile({ attachment }: { attachment: Extract<FileItem, { kind: 'attachment' }>['attachment'] }) {
  const [viewing, setViewing] = useState<number | null>(null)
  return (
    <>
      <button type="button" onClick={() => setViewing(0)} className="block w-full cursor-zoom-in rounded-lg outline-none focus-visible:ring-[3px] focus-visible:ring-ring" aria-label={`View ${attachment.fileName}`}>
        <img src={attachmentUrl(attachment.id)} alt={attachment.fileName} className="w-full rounded-lg border" />
      </button>
      <ImageViewer images={asViewerImages([attachment])} index={viewing} onIndex={setViewing} />
    </>
  )
}

/** A file Argus read from GitLab: its code, and where it lives. */
function RepoViewer({ file }: { file: Extract<FileItem, { kind: 'repo' }> }) {
  const gitlab = useQuery(configQuery).data?.gitlabUrl
  return (
    <div className="grid gap-2">
      <p className="flex min-w-0 items-center gap-1 font-mono text-xs text-muted-foreground">
        <span className="truncate">
          {file.repo && `${file.repo} › `}
          {file.path}
        </span>
        {gitlab && file.repo && (
          <a href={gitlabLink(gitlab, file.repo, file.path, file.branch, null, null)} target="_blank" rel="noopener noreferrer" className="ml-auto flex shrink-0 items-center gap-1 font-sans text-primary-ink underline-offset-2 hover:underline">
            Open in GitLab <ExternalLink className="size-3" aria-hidden="true" />
          </a>
        )}
      </p>
      {file.truncated && <p className="text-xs text-muted-foreground">Argus sent the start of this file only.</p>}
      <CodeBlock code={file.code} lang={file.lang} name={file.path} />
    </div>
  )
}

function TextViewer({ id, name, truncated }: { id: string; name: string; truncated: boolean }) {
  const text = useAttachmentText(id, name, false)
  if (text.isPending) return <Skeleton className="h-64" />
  if (text.error) return <p className="text-sm text-destructive-ink">{text.error.message}</p>
  return (
    <div className="grid gap-2">
      {truncated && <p className="text-xs text-muted-foreground">Cut to fit: this is what the model read.</p>}
      <CodeBlock code={text.data} lang={parseFence(name, '').lang} name={name} />
    </div>
  )
}
