import {
  flexRender,
  getCoreRowModel,
  getFilteredRowModel,
  getPaginationRowModel,
  getSortedRowModel,
  useReactTable,
  type Column,
  type ColumnDef,
  type RowSelectionState,
  type SortingState,
  type Table as TableType,
  type VisibilityState,
} from '@tanstack/react-table'
import { ArrowDown, ArrowUp, ChevronLeft, ChevronRight, ChevronsUpDown, Columns3, Search } from 'lucide-react'
import { useState, type ReactNode } from 'react'
import { ScrollRegion } from '@/components/app/scroll-region'
import { cn } from '@/lib/utils'
import { Button } from './button'
import { Checkbox } from './checkbox'
import { DropdownMenu, DropdownMenuCheckboxItem, DropdownMenuContent, DropdownMenuLabel, DropdownMenuSeparator, DropdownMenuTrigger } from './dropdown-menu'
import { Input } from './input'
import { Skeleton } from './skeleton'

export type { ColumnDef } from '@tanstack/react-table'

/** A sortable column header: click toggles ascending, descending, off. */
export function SortHeader<T>({ column, title }: { column: Column<T>; title: string }) {
  const sorted = column.getIsSorted()
  return (
    <button
      type="button"
      className="-ml-2 inline-flex h-8 items-center gap-1 rounded-md px-2 font-medium hover:bg-accent"
      onClick={() => column.toggleSorting(sorted === 'asc')}
      aria-label={`${title}, sort ${sorted === 'asc' ? 'descending' : 'ascending'}`}
    >
      {title}
      {sorted === 'asc' ? <ArrowUp className="size-3.5" /> : sorted === 'desc' ? <ArrowDown className="size-3.5" /> : <ChevronsUpDown className="size-3.5 opacity-40" />}
    </button>
  )
}

/** The selection column; put it first. */
export function selectColumn<T>(): ColumnDef<T> {
  return {
    id: 'select',
    enableSorting: false,
    enableHiding: false,
    header: ({ table }) => (
      <Checkbox
        checked={table.getIsAllPageRowsSelected() || (table.getIsSomePageRowsSelected() && 'indeterminate')}
        onCheckedChange={(v) => table.toggleAllPageRowsSelected(!!v)}
        aria-label="Select all on this page"
      />
    ),
    cell: ({ row }) => <Checkbox checked={row.getIsSelected()} onCheckedChange={(v) => row.toggleSelected(!!v)} aria-label="Select row" onClick={(e) => e.stopPropagation()} />,
  }
}

export interface DataTableProps<T> {
  columns: ColumnDef<T, never>[] | ColumnDef<T>[]
  data: T[] | undefined
  /** What the rows are, for labels: "people", "events". */
  noun: string
  loading?: boolean
  searchPlaceholder?: string
  empty?: ReactNode
  pageSize?: number
  onRowClick?: (row: T) => void
  getRowId?: (row: T) => string
  /** Rendered above the table when rows are selected: bulk actions. */
  bulk?: (selected: T[], table: TableType<T>) => ReactNode
  /** Extra controls next to the search box: filters, export. */
  toolbar?: ReactNode
  initialSorting?: SortingState
}

export function DataTable<T>({
  columns,
  data,
  noun,
  loading,
  searchPlaceholder,
  empty,
  pageSize = 25,
  onRowClick,
  getRowId,
  bulk,
  toolbar,
  initialSorting = [],
}: DataTableProps<T>) {
  const [sorting, setSorting] = useState<SortingState>(initialSorting)
  const [globalFilter, setGlobalFilter] = useState('')
  const [rowSelection, setRowSelection] = useState<RowSelectionState>({})
  const [columnVisibility, setColumnVisibility] = useState<VisibilityState>({})
  const table = useReactTable({
    data: data ?? [],
    columns: columns as ColumnDef<T>[],
    state: { sorting, globalFilter, rowSelection, columnVisibility },
    onSortingChange: setSorting,
    onGlobalFilterChange: setGlobalFilter,
    onRowSelectionChange: setRowSelection,
    onColumnVisibilityChange: setColumnVisibility,
    getRowId,
    getCoreRowModel: getCoreRowModel(),
    getSortedRowModel: getSortedRowModel(),
    getFilteredRowModel: getFilteredRowModel(),
    getPaginationRowModel: getPaginationRowModel(),
    initialState: { pagination: { pageSize } },
  })
  const selected = table.getSelectedRowModel().rows.map((r) => r.original)
  const rows = table.getRowModel().rows
  const total = table.getFilteredRowModel().rows.length
  const { pageIndex } = table.getState().pagination
  const hideable = table.getAllColumns().filter((c) => c.getCanHide() && typeof c.columnDef.header === 'string')

  return (
    <div className="flex flex-col gap-3">
      <div className="flex flex-wrap items-center gap-2">
        <div className="relative w-full sm:w-72">
          <Search className="pointer-events-none absolute top-1/2 left-2.5 size-4 -translate-y-1/2 text-muted-foreground" aria-hidden="true" />
          <Input
            type="search"
            value={globalFilter}
            onChange={(e) => setGlobalFilter(e.target.value)}
            placeholder={searchPlaceholder ?? `Search ${noun}`}
            aria-label={`Search ${noun}`}
            className="pl-8"
          />
        </div>
        {toolbar}
        {hideable.length > 0 && (
          <DropdownMenu>
            <DropdownMenuTrigger asChild>
              <Button variant="outline" size="sm" className="ml-auto h-9">
                <Columns3 /> Columns
              </Button>
            </DropdownMenuTrigger>
            <DropdownMenuContent align="end">
              <DropdownMenuLabel>Show columns</DropdownMenuLabel>
              <DropdownMenuSeparator />
              {hideable.map((c) => (
                <DropdownMenuCheckboxItem key={c.id} checked={c.getIsVisible()} onCheckedChange={(v) => c.toggleVisibility(!!v)} onSelect={(e) => e.preventDefault()}>
                  {c.columnDef.header as string}
                </DropdownMenuCheckboxItem>
              ))}
            </DropdownMenuContent>
          </DropdownMenu>
        )}
      </div>

      {bulk && selected.length > 0 && (
        <section className="flex flex-wrap items-center gap-2 rounded-lg border border-primary/25 bg-primary/5 px-3 py-2 text-sm" aria-label="Bulk actions">
          <span className="font-medium">{selected.length} selected</span>
          <div className="flex flex-wrap gap-2">{bulk(selected, table)}</div>
          <Button variant="ghost" size="sm" className="ml-auto" onClick={() => table.resetRowSelection()}>
            Clear
          </Button>
        </section>
      )}

      <ScrollRegion label={`${noun[0]!.toUpperCase()}${noun.slice(1)}, table`} className="rounded-xl border bg-card shadow-xs">
        <table className="w-full caption-bottom text-sm">
          <caption className="sr-only">{noun}</caption>
          <thead className="border-b bg-muted/40">
            {table.getHeaderGroups().map((hg) => (
              <tr key={hg.id}>
                {hg.headers.map((h) => (
                  <th
                    key={h.id}
                    scope="col"
                    className={cn('h-10 px-3 text-left align-middle font-medium whitespace-nowrap text-muted-foreground', h.column.id === 'select' && 'w-10')}
                    aria-sort={h.column.getIsSorted() === 'asc' ? 'ascending' : h.column.getIsSorted() === 'desc' ? 'descending' : undefined}
                  >
                    {h.isPlaceholder ? null : flexRender(h.column.columnDef.header, h.getContext())}
                  </th>
                ))}
              </tr>
            ))}
          </thead>
          <tbody>
            {loading &&
              Array.from({ length: 5 }, (_, i) => (
                <tr key={i} className="border-b last:border-0">
                  {table.getVisibleLeafColumns().map((c) => (
                    <td key={c.id} className="px-3 py-3">
                      <Skeleton className="h-4 w-full max-w-40" />
                    </td>
                  ))}
                </tr>
              ))}
            {!loading &&
              rows.map((row) => (
                <tr
                  key={row.id}
                  data-state={row.getIsSelected() ? 'selected' : undefined}
                  className={cn('border-b transition-colors last:border-0 hover:bg-muted/40 data-[state=selected]:bg-primary/5', onRowClick && 'cursor-pointer')}
                  onClick={onRowClick ? () => onRowClick(row.original) : undefined}
                >
                  {row.getVisibleCells().map((cell) => (
                    <td key={cell.id} className="px-3 py-2.5 align-middle">
                      {flexRender(cell.column.columnDef.cell, cell.getContext())}
                    </td>
                  ))}
                </tr>
              ))}
            {!loading && rows.length === 0 && (
              <tr>
                <td colSpan={table.getVisibleLeafColumns().length} className="px-3 py-12 text-center text-muted-foreground">
                  {globalFilter ? `No ${noun} match “${globalFilter}”.` : (empty ?? `No ${noun} yet.`)}
                </td>
              </tr>
            )}
          </tbody>
        </table>
      </ScrollRegion>

      {total > 0 && (
        <div className="flex flex-wrap items-center justify-between gap-2 text-sm text-muted-foreground">
          <span>
            {total} {noun}
            {table.getPageCount() > 1 && ` · page ${pageIndex + 1} of ${table.getPageCount()}`}
          </span>
          {table.getPageCount() > 1 && (
            <div className="flex gap-1">
              <Button variant="outline" size="icon-sm" onClick={() => table.previousPage()} disabled={!table.getCanPreviousPage()} aria-label="Previous page">
                <ChevronLeft />
              </Button>
              <Button variant="outline" size="icon-sm" onClick={() => table.nextPage()} disabled={!table.getCanNextPage()} aria-label="Next page">
                <ChevronRight />
              </Button>
            </div>
          )}
        </div>
      )}
    </div>
  )
}
