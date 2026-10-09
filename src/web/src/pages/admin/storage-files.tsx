import { keepPreviousData, useQuery, useQueryClient } from '@tanstack/react-query'
import { Download, FileStack, Trash2 } from 'lucide-react'
import { useState } from 'react'
import { QueryError } from '@/components/app/query-state'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { useConfirm } from '@/components/ui/confirm'
import { DataTable, SortHeader, selectColumn, type ColumnDef } from '@/components/ui/data-table'
import { EmptyState } from '@/components/ui/empty-state'
import { Input } from '@/components/ui/input'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { ago, when } from '@/lib/format'
import { useDebounced } from '@/lib/use-debounced'
import { ANY, fileSearch, kinds, noFilter, origins, plural, size, states, type Amount, type FileList, type FileQuery, type FileRow, type PersonFiles } from './storage-api'

const larger = [
  { value: ANY, label: 'Any size' },
  { value: String(1024 * 1024), label: 'Over 1 MB' },
  { value: String(10 * 1024 * 1024), label: 'Over 10 MB' },
  { value: String(100 * 1024 * 1024), label: 'Over 100 MB' },
]

const older = [
  { value: ANY, label: 'Any age' },
  { value: '7', label: 'Older than a week' },
  { value: '30', label: 'Older than 30 days' },
  { value: '90', label: 'Older than 90 days' },
  { value: '365', label: 'Older than a year' },
]

const sorts = [
  { value: 'size', label: 'Largest first' },
  { value: 'new', label: 'Newest first' },
  { value: 'old', label: 'Oldest first' },
]

const columns: ColumnDef<FileRow>[] = [
  selectColumn<FileRow>(),
  {
    id: 'name',
    accessorFn: (f) => `${f.name} ${f.chatTitle ?? ''}`,
    header: ({ column }) => <SortHeader column={column} title="File" />,
    sortingFn: (a, b) => a.original.name.localeCompare(b.original.name),
    cell: ({ row: { original: f } }) => (
      <div className="min-w-48">
        <p className="font-medium break-all">{f.name}</p>
        <p className="text-xs text-muted-foreground">
          {f.chatTitle ? `in “${f.chatTitle}”` : states[f.state]}
          {f.chatTitle && f.state === 'deleted' ? ' (deleted)' : ''}
        </p>
      </div>
    ),
  },
  {
    id: 'person',
    accessorFn: (f) => `${f.person} ${f.email ?? ''}`,
    header: ({ column }) => <SortHeader column={column} title="Whose" />,
    sortingFn: (a, b) => a.original.person.localeCompare(b.original.person),
    cell: ({ row: { original: f } }) => (
      <span className="inline-flex items-center gap-1.5 whitespace-nowrap">
        {f.person}
        {f.held && <Badge variant="warning">legal hold</Badge>}
      </span>
    ),
  },
  {
    id: 'origin',
    accessorFn: (f) => `${origins[f.origin]} ${f.kind} ${states[f.state]}`,
    header: 'From',
    cell: ({ row: { original: f } }) => (
      <span className="flex flex-wrap gap-1">
        <Badge variant="secondary">{origins[f.origin]}</Badge>
        <Badge variant="outline">{f.kind}</Badge>
      </span>
    ),
  },
  {
    id: 'bytes',
    accessorFn: (f) => f.bytes,
    header: ({ column }) => <SortHeader column={column} title="Size" />,
    cell: ({ row: { original: f } }) => <span className="whitespace-nowrap tabular-nums">{size(f.bytes)}</span>,
  },
  {
    id: 'at',
    accessorFn: (f) => new Date(f.createdAt).getTime(),
    header: ({ column }) => <SortHeader column={column} title="When" />,
    cell: ({ row: { original: f } }) => (
      <time dateTime={f.createdAt} title={when(f.createdAt)} className="whitespace-nowrap text-muted-foreground">
        {ago(f.createdAt)}
      </time>
    ),
  },
  {
    id: 'download',
    enableSorting: false,
    enableHiding: false,
    header: () => <span className="sr-only">Download</span>,
    cell: ({ row: { original: f } }) => (
      <Button variant="ghost" size="icon-sm" asChild>
        <a href={`/api/admin/storage/files/${f.id}/download`} download aria-label={`Download ${f.name}`} onClick={(e) => e.stopPropagation()}>
          <Download />
        </a>
      </Button>
    ),
  },
]

/** A select of a few fixed choices, labelled for the screen reader. */
function Pick({ label, value, onChange, options, className }: { label: string; value: string; onChange: (v: string) => void; options: { value: string; label: string }[]; className?: string }) {
  return (
    <Select value={value} onValueChange={onChange}>
      <SelectTrigger className={className ?? 'h-9 w-44'} aria-label={label}>
        <SelectValue />
      </SelectTrigger>
      <SelectContent>
        {options.map((o) => (
          <SelectItem key={o.value} value={o.value}>
            {o.label}
          </SelectItem>
        ))}
      </SelectContent>
    </Select>
  )
}

/**
 * The chat's files and what the tools made: whose, which chat, what, how big and when. Filtered on the
 * server (the newest thousand of what matches at most), sorted here; downloaded (audited) and deleted
 * by the selection, never one of a person on legal hold.
 */
export function StorageFiles() {
  const [f, setF] = useState<FileQuery>(noFilter)
  const set = (key: keyof FileQuery) => (value: string) => setF((x) => ({ ...x, [key]: value }))
  const people = useQuery({
    queryKey: ['admin', 'storage', 'people'],
    queryFn: ({ signal }) => api<{ people: PersonFiles[] }>('/api/admin/storage/people', { signal }),
  })
  // The search box asks the server once typing pauses, not at each key: each ask reads every chat's files.
  const q = useDebounced(f.q, 300)
  const search = fileSearch({ ...f, q })
  const list = useQuery({
    queryKey: ['admin', 'storage', 'files', search],
    queryFn: ({ signal }) => api<FileList>(`/api/admin/storage/files${search}`, { signal }),
    placeholderData: keepPreviousData,
  })
  const remove = useDeleteFiles()
  const d = list.data
  return (
    <div className="grid gap-4">
      <div className="flex flex-wrap items-end gap-2">
        <Input type="search" value={f.q} onChange={(e) => set('q')(e.target.value)} placeholder="File, person or chat" aria-label="Find files by name, person or chat" className="h-9 w-full sm:w-64" />
        <Pick
          label="Person"
          value={f.person}
          onChange={set('person')}
          className="h-9 w-52"
          options={[{ value: ANY, label: 'Everyone' }, ...(people.data?.people ?? []).filter((p) => p.count > 0).map((p) => ({ value: p.id, label: `${p.displayName || p.userName} (${size(p.bytes)})` }))]}
        />
        <Pick label="Kind" value={f.kind} onChange={set('kind')} className="h-9 w-32" options={[{ value: ANY, label: 'Every kind' }, ...kinds.map((k) => ({ value: k, label: k }))]} />
        <Pick label="From" value={f.origin} onChange={set('origin')} options={[{ value: ANY, label: 'Uploaded or made' }, ...Object.entries(origins).map(([value, label]) => ({ value, label }))]} />
        <Pick label="Where" value={f.state} onChange={set('state')} options={[{ value: ANY, label: 'Anywhere' }, ...Object.entries(states).map(([value, label]) => ({ value, label }))]} />
        <Pick label="Size" value={f.min} onChange={set('min')} className="h-9 w-36" options={larger} />
        <Pick label="Age" value={f.days} onChange={set('days')} options={older} />
        <Pick label="Order" value={f.sort} onChange={set('sort')} className="h-9 w-36" options={sorts} />
        {JSON.stringify(f) !== JSON.stringify(noFilter) && (
          <Button variant="ghost" size="sm" onClick={() => setF(noFilter)}>
            Clear filters
          </Button>
        )}
      </div>
      {/* A list that failed is its error alone: not the rows of other filters, nor "No files here". */}
      {list.error ? (
        <QueryError error={list.error} retry={() => list.refetch()} />
      ) : (
        <>
          {d && (
            <p className="text-sm text-muted-foreground">
              {plural(d.total.count, 'file')} match, taking <span className="font-medium text-foreground">{size(d.total.bytes)}</span>.
              {d.capped && ` The table holds the first ${d.rows.length.toLocaleString('en-US')} in this order; narrow the filters to see the rest.`}
            </p>
          )}
          <DataTable
            columns={columns}
            data={d?.rows}
            loading={list.isPending}
            noun="files"
            getRowId={(r) => r.id}
            searchPlaceholder="Search these files"
            initialSorting={[]}
            empty={
              <EmptyState icon={FileStack} title="No files here" className="py-8">
                Uploads and what the tools make appear here. Try fewer filters.
              </EmptyState>
            }
            bulk={(selected, table) => (
              <Button size="sm" variant="destructive" loading={remove.pending} onClick={() => void remove.run(selected, () => table.resetRowSelection())}>
                <Trash2 /> Delete {plural(selected.length, 'file')}
              </Button>
            )}
          />
        </>
      )}
    </div>
  )
}

/** Deletes the chosen files after asking, says what went and what legal hold kept. */
function useDeleteFiles() {
  const confirm = useConfirm()
  const client = useQueryClient()
  const [pending, setPending] = useState(false)
  const run = async (files: FileRow[], done: () => void) => {
    const bytes = files.reduce((a, f) => a + f.bytes, 0)
    const held = files.filter((f) => f.held).length
    const inChats = files.filter((f) => f.state === 'chat' || f.state === 'assistant').length
    const ok = await confirm({
      title: `Delete ${plural(files.length, 'file')} (${size(bytes)})?`,
      description: [
        'They are gone for good; this cannot be undone.',
        inChats ? `${plural(inChats, 'file')} ${inChats === 1 ? 'is' : 'are'} in chats or assistants: they keep their words, without the file.` : null,
        held ? `${plural(held, 'file')} of people on legal hold ${held === 1 ? 'stays' : 'stay'}.` : null,
      ].filter(Boolean).join(' '),
      confirm: 'Delete',
      destructive: true,
    })
    if (!ok) return
    setPending(true)
    try {
      const res = await api<{ deleted: Amount; held: Amount }>('/api/admin/storage/files/delete', { body: { ids: files.map((f) => f.id) } })
      toast.success(`Deleted ${plural(res.deleted.count, 'file')}, ${size(res.deleted.bytes)} freed`, {
        description: res.held.count ? `${plural(res.held.count, 'file')} kept: legal hold.` : undefined,
      })
      done()
      await client.invalidateQueries({ queryKey: ['admin', 'storage'] })
    } catch (e) {
      toast.error(errorMessage(e))
    } finally {
      setPending(false)
    }
  }
  return { run, pending }
}

