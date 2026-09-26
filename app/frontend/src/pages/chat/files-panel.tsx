import { useQuery } from '@tanstack/react-query'
import { ArrowLeft, Download, ExternalLink, FileCode2, FileDown, FileSearch, FileText, Image as ImageIcon, X } from 'lucide-react'
import { useState } from 'react'
import { Button } from '@/components/ui/button'
import { EmptyState } from '@/components/ui/empty-state'
import { Skeleton } from '@/components/ui/skeleton'
import { formatValue } from '@/lib/format'
import { cn } from '@/lib/utils'
import { attachmentUrl, configQuery, downloadUrl } from './api'
import { gitlabLink } from './argus'
import { CodeBlock } from './code-block'
import type { FileItem } from './files'
import { parseFence } from './files'
import { ImageViewer } from './image-viewer'

/** Like Claude's: every file in the branch on screen, and a viewer for the one chosen. */
export function FilesPanel({ files, selected, onSelect, onClose }: { files: FileItem[]; selected: string | null; onSelect: (key: string | null) => void; onClose: () => void }) {
  const current = files.find((f) => f.key === selected) ?? null
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
        <Button variant="ghost" size="icon-sm" className="ml-auto" onClick={onClose} aria-label="Close files">
          <X />
        </Button>
      </header>
      <div className="min-h-0 flex-1 overflow-y-auto p-3">
        {current ? (
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
                    {f.kind === 'code' ? <FileCode2 className="size-4" /> : f.kind === 'repo' ? <FileSearch className="size-4" /> : f.attachment.kind === 'image' ? <ImageIcon className="size-4" /> : f.attachment.kind === 'file' ? <FileDown className="size-4" /> : <FileText className="size-4" />}
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

function Viewer({ file }: { file: FileItem }) {
  if (file.kind === 'code') return <CodeBlock code={file.code} lang={file.lang} name={file.name} />
  if (file.kind === 'repo') return <RepoViewer file={file} />
  if (file.attachment.kind === 'image') return <ImageFile attachment={file.attachment} />
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
      <ImageViewer images={[attachment]} index={viewing} onIndex={setViewing} />
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
  const text = useQuery({
    queryKey: ['chat', 'attachment', id],
    queryFn: async ({ signal }) => {
      const res = await fetch(attachmentUrl(id), { signal, credentials: 'same-origin', headers: { 'X-Requested-With': 'fetch' } })
      if (!res.ok) throw new Error(`Could not open ${name} (HTTP ${res.status}).`)
      return res.text()
    },
    staleTime: Infinity,
  })
  if (text.isPending) return <Skeleton className="h-64" />
  if (text.error) return <p className="text-sm text-destructive-ink">{text.error.message}</p>
  return (
    <div className="grid gap-2">
      {truncated && <p className="text-xs text-muted-foreground">Cut to fit: this is what the model read.</p>}
      <CodeBlock code={text.data} lang={parseFence(name, '').lang} name={name} />
    </div>
  )
}
