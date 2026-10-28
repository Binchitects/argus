import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { HardDrive, Trash2 } from 'lucide-react'
import { useState } from 'react'
import { useOutletContext } from 'react-router'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { useConfirm } from '@/components/ui/confirm'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage, type Me } from '@/lib/api'
import { bytes } from './model-profile'

export interface DiskItem {
  path: string
  /** gguf; snapshot: a Hugging Face folder, for vLLM; file: a picture or video model's (safetensors). */
  kind: 'gguf' | 'snapshot' | 'file'
  size: number
  parts: number
  usedBy: string[]
  /** A server of the stack whose own file it is (never deleted here). */
  keptBy: string | null
  downloading: boolean
  /** Why it cannot be deleted; null: it can. */
  whyNot: string | null
}

interface Disk {
  items: DiskItem[]
  used: number
  free: number | null
  total: number | null
}

const kindLabel = { gguf: 'GGUF', snapshot: 'Snapshot', file: 'File' } as const

/** Whether the person may delete models (and so their files). */
const mayDelete = (me: Me | undefined) => !!me && (!!me.isAdmin || ['models.manage', 'models.delete'].some((p) => me.permissions?.includes(p)))

/** Admin → Models → On disk: what the model library holds, which models use each, and deleting what nothing uses. */
export function OnDiskCard() {
  const me = useOutletContext<Me | undefined>()
  const queryClient = useQueryClient()
  const confirm = useConfirm()
  const [all, setAll] = useState(false)
  const disk = useQuery({ queryKey: ['admin', 'models', 'disk'], queryFn: ({ signal }) => api<Disk>('/api/admin/models/library/disk', { signal }) })
  const remove = useMutation({
    mutationFn: (path: string) => api<{ path: string; freed: number }>(`/api/admin/models/library/disk?${new URLSearchParams({ path })}`, { method: 'DELETE' }),
    onSuccess: (r) => {
      toast.success(`${r.path} deleted`, { description: `${bytes(r.freed)} freed.` })
      void queryClient.invalidateQueries({ queryKey: ['admin', 'models'] })
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  if (!disk.data) return null
  const { items, used, free, total } = disk.data
  const unused = items.filter((i) => i.whyNot === null)
  const shown = all ? items : items.filter((i) => i.keptBy === null)
  const deletes = mayDelete(me)
  return (
    <Card className="mt-6">
      <CardHeader>
        <CardTitle className="flex items-center gap-2 text-base">
          <HardDrive className="size-4" aria-hidden="true" /> On disk
        </CardTitle>
        <CardDescription>
          The model library takes {bytes(used)}
          {free !== null && total !== null && ` of its disk’s ${bytes(total)}, ${bytes(free)} free`}.{' '}
          {unused.length > 0
            ? `${unused.length} ${unused.length === 1 ? 'is' : 'are'} used by no model (${bytes(unused.reduce((n, i) => n + i.size, 0))}): deleting one frees its room for good; download it again to have it back.`
            : 'Every file is used by a model.'}{' '}
          Removing a model leaves its files here unless you delete them with it.
        </CardDescription>
      </CardHeader>
      <CardContent>
        <div className="overflow-x-auto">
          <table className="w-full text-sm" aria-label="On disk">
            <thead>
              <tr className="text-left text-xs text-muted-foreground">
                <th className="py-1.5 pr-3 font-medium">File</th>
                <th className="py-1.5 pr-3 font-medium">Kind</th>
                <th className="py-1.5 pr-3 text-right font-medium">Size</th>
                <th className="py-1.5 pr-3 font-medium">Used by</th>
                <th className="py-1.5 font-medium">
                  <span className="sr-only">Delete</span>
                </th>
              </tr>
            </thead>
            <tbody>
              {shown.map((i) => (
                <tr key={i.path} className="border-t align-top">
                  <td className="py-1.5 pr-3 font-mono text-xs [overflow-wrap:anywhere]">
                    {i.path}
                    {i.parts > 1 && <span className="font-sans text-muted-foreground"> ({i.parts} parts)</span>}
                  </td>
                  <td className="py-1.5 pr-3">
                    <Badge variant="outline">{kindLabel[i.kind]}</Badge>
                  </td>
                  <td className="py-1.5 pr-3 text-right tabular-nums">{bytes(i.size)}</td>
                  <td className="py-1.5 pr-3">
                    {i.usedBy.length > 0 ? i.usedBy.join(', ') : i.keptBy ? <span className="text-muted-foreground">{i.keptBy}</span> : i.downloading ? <span className="text-muted-foreground">downloading</span> : <span className="text-muted-foreground">no model</span>}
                  </td>
                  <td className="py-1 text-right">
                    {deletes && (
                      <Button
                        size="sm"
                        variant="ghost"
                        className="text-destructive-ink"
                        disabled={i.whyNot !== null || remove.isPending}
                        title={i.whyNot ?? `Delete ${i.path} from disk`}
                        aria-label={`Delete ${i.path} from disk`}
                        onClick={async () => {
                          const where = i.kind === 'snapshot' ? ' in its folder' : i.parts > 1 ? ` in ${i.parts} parts` : ''
                          const description = `${bytes(i.size)}${where} are deleted for good. Download it again to have it back.`
                          if (await confirm({ title: `Delete ${i.path} from disk?`, description, confirm: 'Delete', destructive: true })) remove.mutate(i.path)
                        }}
                      >
                        <Trash2 />
                      </Button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        {items.some((i) => i.keptBy !== null) && (
          <Button variant="link" size="sm" className="mt-2 px-0" onClick={() => setAll(!all)}>
            {all ? 'Hide the servers’ own files' : `Show the servers’ own files too (${items.filter((i) => i.keptBy !== null).length})`}
          </Button>
        )}
      </CardContent>
    </Card>
  )
}
