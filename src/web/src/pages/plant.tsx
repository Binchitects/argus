import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Factory, FileUp, Plus, Trash2 } from 'lucide-react'
import { useState, type ReactNode } from 'react'
import { Link, useNavigate, useParams } from 'react-router'
import { PageHeader } from '@/components/app/page-header'
import { QueryError } from '@/components/app/query-state'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { useConfirm } from '@/components/ui/confirm'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { EmptyState } from '@/components/ui/empty-state'
import { Field } from '@/components/ui/field'
import { Input, Textarea } from '@/components/ui/input'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Skeleton } from '@/components/ui/skeleton'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { areaTree, limits, plantAssetQuery, shown, plantQuery, plantSiteQuery, type PlantAsset, type PlantImported, type PlantKinds } from './plant-api'

const POINTS_SHEET = 'area,asset,kind,point,unit,type,source,address,low,high\nLine 1,Pump 1,machine,Outlet temperature,°C,number,opcua,ns=2;s=P1.T,10,80'

/** Workspace → Plant (v9): the sites, their areas, assets and points, set up by hand or from a sheet. */
export function PlantPage() {
  const overview = useQuery(plantQuery)
  const [adding, setAdding] = useState(false)
  const may = overview.data?.mayManage ?? false
  return (
    <>
      <PageHeader
        title="Plant"
        description="Your sites, what is in them and what each thing measures: areas and lines, the machines, sensors and cameras in them, and their points with units, sources and limits."
        actions={
          may && (
            <Button onClick={() => setAdding(true)}>
              <Plus /> New site
            </Button>
          )
        }
      />
      {overview.error && <QueryError error={overview.error} retry={() => overview.refetch()} />}
      {overview.isPending && <Skeleton className="h-40" />}
      {overview.data?.sites.length === 0 && (
        <EmptyState icon={Factory} title="No sites yet" action={may && <Button onClick={() => setAdding(true)}>New site</Button>}>
          {may ? 'Add a site, then its areas and assets, or import its points from a sheet.' : 'No site has been set up for you yet.'}
        </EmptyState>
      )}
      <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
        {overview.data?.sites.map((s) => (
          <Link key={s.id} to={`/plant/${s.id}`} className="rounded-xl outline-none focus-visible:ring-[3px] focus-visible:ring-ring">
            <Card className="h-full hover:bg-accent/50">
              <CardHeader>
                <CardTitle className="text-base">{s.name}</CardTitle>
                {s.description && <CardDescription>{s.description}</CardDescription>}
              </CardHeader>
              <CardContent className="flex flex-wrap gap-2 text-sm text-muted-foreground">
                <span>{s.assets.toLocaleString()} assets</span>
                <span>·</span>
                <span>{s.points.toLocaleString()} points</span>
                <span>·</span>
                <span>{s.timeZone}</span>
              </CardContent>
            </Card>
          </Link>
        ))}
      </div>
      <Dialog open={adding} onOpenChange={setAdding}>
        <DialogContent className="sm:max-w-md">{adding && <SiteForm onClose={() => setAdding(false)} />}</DialogContent>
      </Dialog>
    </>
  )
}

function FormShell({ title, description, error, children, onClose, busy, ready, onSubmit }: { title: string; description?: string; error: string | null; children: ReactNode; onClose: () => void; busy: boolean; ready: boolean; onSubmit: () => void }) {
  return (
    <form
      className="grid gap-4"
      onSubmit={(e) => {
        e.preventDefault()
        onSubmit()
      }}
    >
      <DialogHeader>
        <DialogTitle>{title}</DialogTitle>
        {description && <DialogDescription>{description}</DialogDescription>}
      </DialogHeader>
      {error && <Alert variant="destructive">{error}</Alert>}
      {children}
      <DialogFooter>
        <Button type="button" variant="outline" onClick={onClose}>
          Cancel
        </Button>
        <Button type="submit" loading={busy} disabled={!ready}>
          Save
        </Button>
      </DialogFooter>
    </form>
  )
}

function Choice({ value, onChange, options, label }: { value: string; onChange: (v: string) => void; options: string[]; label: string }) {
  return (
    <Select value={value} onValueChange={onChange}>
      <SelectTrigger aria-label={label}>
        <SelectValue />
      </SelectTrigger>
      <SelectContent>
        {options.map((o) => (
          <SelectItem key={o} value={o}>
            {o}
          </SelectItem>
        ))}
      </SelectContent>
    </Select>
  )
}

/** Saves through `run`, then refreshes the plant's queries and closes. */
function useSave(run: () => Promise<unknown>, onClose: () => void) {
  const queryClient = useQueryClient()
  const [error, setError] = useState<string | null>(null)
  const save = useMutation({
    mutationFn: run,
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['plant'] })
      onClose()
    },
    onError: (e) => setError(errorMessage(e)),
  })
  return { error, busy: save.isPending, submit: () => (setError(null), save.mutate()) }
}

function SiteForm({ onClose }: { onClose: () => void }) {
  const navigate = useNavigate()
  const [name, setName] = useState('')
  const [description, setDescription] = useState('')
  const [timeZone, setTimeZone] = useState(Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC')
  const s = useSave(async () => {
    const made = await api<{ id: string }>('/api/plant/sites', { body: { name: name.trim(), description: description.trim() || null, timeZone } })
    void navigate(`/plant/${made.id}`)
  }, onClose)
  return (
    <FormShell title="New site" error={s.error} onClose={onClose} busy={s.busy} ready={!!name.trim()} onSubmit={s.submit}>
      <Field label="Name">
        <Input value={name} maxLength={200} onChange={(e) => setName(e.target.value)} placeholder="Plant north" />
      </Field>
      <Field label="What it is (optional)">
        <Input value={description} maxLength={2000} onChange={(e) => setDescription(e.target.value)} />
      </Field>
      <Field label="Time zone" hint="Its shifts and reports are in it.">
        <Input value={timeZone} onChange={(e) => setTimeZone(e.target.value)} placeholder="Europe/Berlin" />
      </Field>
    </FormShell>
  )
}

function AreaForm({ siteId, parentId, kinds, onClose }: { siteId: string; parentId: string | null; kinds: PlantKinds; onClose: () => void }) {
  const [name, setName] = useState('')
  const [kind, setKind] = useState(kinds.areas[0] ?? 'area')
  const s = useSave(() => api(`/api/plant/sites/${siteId}/areas`, { body: { name: name.trim(), kind, parentId } }), onClose)
  return (
    <FormShell title={parentId ? 'New area inside' : 'New area'} error={s.error} onClose={onClose} busy={s.busy} ready={!!name.trim()} onSubmit={s.submit}>
      <Field label="Name">
        <Input value={name} maxLength={200} onChange={(e) => setName(e.target.value)} placeholder="Line 3" />
      </Field>
      <Field label="Kind">
        <Choice label="Kind" value={kind} onChange={setKind} options={kinds.areas} />
      </Field>
    </FormShell>
  )
}

function AssetForm({ siteId, areaId, kinds, onClose }: { siteId: string; areaId: string | null; kinds: PlantKinds; onClose: () => void }) {
  const [name, setName] = useState('')
  const [kind, setKind] = useState(kinds.assets[0] ?? 'machine')
  const [vendor, setVendor] = useState('')
  const [model, setModel] = useState('')
  const s = useSave(() => api(`/api/plant/sites/${siteId}/assets`, { body: { name: name.trim(), kind, areaId, vendor: vendor.trim() || null, model: model.trim() || null } }), onClose)
  return (
    <FormShell title="New asset" error={s.error} onClose={onClose} busy={s.busy} ready={!!name.trim()} onSubmit={s.submit}>
      <Field label="Name">
        <Input value={name} maxLength={200} onChange={(e) => setName(e.target.value)} placeholder="Pump 1" />
      </Field>
      <Field label="Kind">
        <Choice label="Kind" value={kind} onChange={setKind} options={kinds.assets} />
      </Field>
      <div className="grid grid-cols-2 gap-3">
        <Field label="Vendor (optional)">
          <Input value={vendor} maxLength={200} onChange={(e) => setVendor(e.target.value)} />
        </Field>
        <Field label="Model (optional)">
          <Input value={model} maxLength={200} onChange={(e) => setModel(e.target.value)} />
        </Field>
      </div>
    </FormShell>
  )
}

const num = (v: string) => (v.trim() === '' ? null : Number(v))

function PointForm({ assetId, kinds, onClose }: { assetId: string; kinds: PlantKinds; onClose: () => void }) {
  const [name, setName] = useState('')
  const [dataType, setDataType] = useState(kinds.types[0] ?? 'number')
  const [unit, setUnit] = useState('')
  const [source, setSource] = useState(kinds.sources[0] ?? 'manual')
  const [address, setAddress] = useState('')
  const [low, setLow] = useState('')
  const [high, setHigh] = useState('')
  const bad = [low, high].some((v) => v.trim() !== '' && Number.isNaN(Number(v)))
  const s = useSave(
    () => api(`/api/plant/assets/${assetId}/points`, { body: { name: name.trim(), dataType, unit: unit.trim() || null, source, address: address.trim() || null, low: num(low), high: num(high) } }),
    onClose,
  )
  return (
    <FormShell title="New point" description="A value the asset gives: a temperature, a state, a count." error={s.error} onClose={onClose} busy={s.busy} ready={!!name.trim() && !bad} onSubmit={s.submit}>
      <Field label="Name">
        <Input value={name} maxLength={200} onChange={(e) => setName(e.target.value)} placeholder="Outlet temperature" />
      </Field>
      <div className="grid grid-cols-2 gap-3">
        <Field label="Type">
          <Choice label="Type" value={dataType} onChange={setDataType} options={kinds.types} />
        </Field>
        <Field label="Unit (optional)">
          <Input value={unit} maxLength={40} onChange={(e) => setUnit(e.target.value)} placeholder="°C" />
        </Field>
        <Field label="Source">
          <Choice label="Source" value={source} onChange={setSource} options={kinds.sources} />
        </Field>
        <Field label="Address (optional)">
          <Input value={address} maxLength={500} onChange={(e) => setAddress(e.target.value)} placeholder="ns=2;s=P1.T" />
        </Field>
        <Field label="Low (optional)">
          <Input value={low} inputMode="decimal" onChange={(e) => setLow(e.target.value)} />
        </Field>
        <Field label="High (optional)">
          <Input value={high} inputMode="decimal" onChange={(e) => setHigh(e.target.value)} />
        </Field>
      </div>
    </FormShell>
  )
}

function ImportForm({ siteId, onClose }: { siteId: string; onClose: () => void }) {
  const [csv, setCsv] = useState('')
  const s = useSave(async () => {
    const r = await api<PlantImported>(`/api/plant/sites/${siteId}/import`, { body: { csv } })
    toast.success(`Imported: ${r.areas} areas, ${r.assets} assets and ${r.points} points new, from ${r.sheetLines} lines.`)
  }, onClose)
  return (
    <FormShell
      title="Import points from a sheet"
      description="A line a point, its header first. Areas and assets that are not there yet are made; points already there are updated. Nothing is kept if a line is wrong."
      error={s.error}
      onClose={onClose}
      busy={s.busy}
      ready={!!csv.trim()}
      onSubmit={s.submit}
    >
      <Field label="The sheet (CSV)" hint="Columns: area, asset, kind, point, unit, type, source, address, low, high — asset and point are needed.">
        <Textarea rows={10} className="font-mono text-xs" value={csv} onChange={(e) => setCsv(e.target.value)} placeholder={POINTS_SHEET} />
      </Field>
      <input
        type="file"
        accept=".csv,text/csv"
        aria-label="Choose a CSV file"
        className="text-sm"
        onChange={async (e) => {
          const f = e.target.files?.[0]
          if (f) setCsv(await f.text())
        }}
      />
    </FormShell>
  )
}

type Adding = { what: 'area'; parentId: string | null } | { what: 'asset'; areaId: string | null } | { what: 'point'; assetId: string } | { what: 'import' }

/** A site whole: its areas as a tree with their assets, and the chosen asset's points. */
export function PlantSitePage() {
  const { id = '' } = useParams()
  const navigate = useNavigate()
  const confirm = useConfirm()
  const queryClient = useQueryClient()
  const overview = useQuery(plantQuery)
  const site = useQuery(plantSiteQuery(id))
  const [chosen, setChosen] = useState<string | null>(null)
  const [adding, setAdding] = useState<Adding | null>(null)
  const may = overview.data?.mayManage ?? false
  const kinds = overview.data?.kinds
  const remove = useMutation({
    mutationFn: (path: string) => api(path, { method: 'DELETE' }),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: ['plant'] }),
    onError: (e) => toast.error(errorMessage(e)),
  })
  const ask = async (title: string, description: string, path: string, after?: () => void) => {
    if (await confirm({ title, description, confirm: 'Delete', destructive: true })) remove.mutate(path, { onSuccess: after })
  }
  if (site.error) return <QueryError error={site.error} retry={() => site.refetch()} />
  if (!site.data) return <Skeleton className="h-60" />
  const { areas, assets } = site.data
  const tree = areaTree(areas)
  const inArea = (areaId: string | null) => assets.filter((a) => (areaId ? a.areaId === areaId : !a.areaId || !areas.some((x) => x.id === a.areaId)))
  const loose = inArea(null)
  return (
    <>
      <PageHeader
        title={site.data.site.name}
        description={site.data.site.description ?? `${site.data.site.assets.toLocaleString()} assets and ${site.data.site.points.toLocaleString()} points, in ${site.data.site.timeZone}.`}
        actions={
          may && (
            <div className="flex flex-wrap gap-2">
              <Button variant="outline" onClick={() => setAdding({ what: 'import' })}>
                <FileUp /> Import a sheet
              </Button>
              <Button variant="outline" onClick={() => setAdding({ what: 'area', parentId: null })}>
                <Plus /> Area
              </Button>
              <Button onClick={() => setAdding({ what: 'asset', areaId: null })}>
                <Plus /> Asset
              </Button>
              <Button
                variant="ghost"
                size="icon"
                className="text-destructive-ink"
                aria-label={`Delete ${site.data.site.name}`}
                onClick={() => ask(`Delete ${site.data!.site.name}?`, 'Its areas, assets and points go with it.', `/api/plant/sites/${id}`, () => void navigate('/plant'))}
              >
                <Trash2 />
              </Button>
            </div>
          )
        }
      />
      {areas.length === 0 && assets.length === 0 && (
        <EmptyState icon={Factory} title="Nothing in this site yet" action={may && <Button onClick={() => setAdding({ what: 'import' })}>Import a sheet</Button>}>
          {may ? 'Add its areas and assets, or import its points from a sheet.' : 'Nothing has been set up here yet.'}
        </EmptyState>
      )}
      <div className="grid gap-4 lg:grid-cols-[minmax(16rem,22rem)_1fr]">
        <nav aria-label="Areas and assets" className="grid content-start gap-1 text-sm">
          {tree.map(({ area, depth }) => (
            <div key={area.id} style={{ paddingInlineStart: `${depth * 1}rem` }}>
              <div className="group flex items-center gap-1 py-1 font-medium">
                <span className="min-w-0 flex-1 truncate">{area.name}</span>
                <Badge variant="outline">{area.kind}</Badge>
                {may && (
                  <>
                    <Button size="icon-sm" variant="ghost" aria-label={`New area in ${area.name}`} onClick={() => setAdding({ what: 'area', parentId: area.id })}>
                      <Plus />
                    </Button>
                    <Button size="icon-sm" variant="ghost" aria-label={`New asset in ${area.name}`} onClick={() => setAdding({ what: 'asset', areaId: area.id })}>
                      <Factory />
                    </Button>
                    <Button size="icon-sm" variant="ghost" className="text-destructive-ink" aria-label={`Delete ${area.name}`} onClick={() => ask(`Delete ${area.name}?`, 'The areas inside it move up a level; its assets stay in the site, outside any area.', `/api/plant/areas/${area.id}`)}>
                      <Trash2 />
                    </Button>
                  </>
                )}
              </div>
              <AssetList assets={inArea(area.id)} chosen={chosen} onChoose={setChosen} />
            </div>
          ))}
          {loose.length > 0 && (
            <div>
              {tree.length > 0 && <div className="py-1 font-medium text-muted-foreground">Outside any area</div>}
              <AssetList assets={loose} chosen={chosen} onChoose={setChosen} />
            </div>
          )}
        </nav>
        <section aria-label="The asset">
          {chosen && assets.some((a) => a.id === chosen) ? (
            <AssetPanel id={chosen} may={may} onAddPoint={() => setAdding({ what: 'point', assetId: chosen })} onDelete={ask} />
          ) : (
            assets.length > 0 && <p className="text-sm text-muted-foreground">Choose an asset to see its points.</p>
          )}
        </section>
      </div>
      <Dialog open={adding !== null} onOpenChange={(o) => !o && setAdding(null)}>
        <DialogContent className={adding?.what === 'import' ? 'sm:max-w-2xl' : 'sm:max-w-md'}>
          {adding && kinds && adding.what === 'area' && <AreaForm siteId={id} parentId={adding.parentId} kinds={kinds} onClose={() => setAdding(null)} />}
          {adding && kinds && adding.what === 'asset' && <AssetForm siteId={id} areaId={adding.areaId} kinds={kinds} onClose={() => setAdding(null)} />}
          {adding && kinds && adding.what === 'point' && <PointForm assetId={adding.assetId} kinds={kinds} onClose={() => setAdding(null)} />}
          {adding?.what === 'import' && <ImportForm siteId={id} onClose={() => setAdding(null)} />}
        </DialogContent>
      </Dialog>
    </>
  )
}

function AssetList({ assets, chosen, onChoose }: { assets: PlantAsset[]; chosen: string | null; onChoose: (id: string) => void }) {
  if (assets.length === 0) return null
  return (
    <ul className="grid gap-0.5 ps-3">
      {assets.map((a) => (
        <li key={a.id}>
          <button
            type="button"
            aria-current={chosen === a.id || undefined}
            onClick={() => onChoose(a.id)}
            className="flex w-full items-center gap-2 rounded-md px-2 py-1 text-start outline-none hover:bg-accent/50 focus-visible:ring-[3px] focus-visible:ring-ring aria-[current]:bg-accent"
          >
            <span className="min-w-0 flex-1 truncate">{a.name}</span>
            <span className="text-xs text-muted-foreground">
              {a.kind} · {a.points}
            </span>
          </button>
        </li>
      ))}
    </ul>
  )
}

function AssetPanel({ id, may, onAddPoint, onDelete }: { id: string; may: boolean; onAddPoint: () => void; onDelete: (title: string, description: string, path: string) => void }) {
  const q = useQuery(plantAssetQuery(id))
  if (q.error) return <QueryError error={q.error} retry={() => q.refetch()} />
  if (!q.data) return <Skeleton className="h-40" />
  const { asset, points } = q.data
  const about = [asset.kind, asset.vendor, asset.model, asset.serial && `serial ${asset.serial}`].filter(Boolean).join(' · ')
  return (
    <Card>
      <CardHeader className="flex flex-row items-start gap-2">
        <div className="min-w-0 flex-1">
          <CardTitle className="text-base">{asset.name}</CardTitle>
          <CardDescription>{about}</CardDescription>
          {asset.description && <p className="mt-1 text-sm">{asset.description}</p>}
        </div>
        {may && (
          <>
            <Button size="sm" variant="outline" onClick={onAddPoint}>
              <Plus /> Point
            </Button>
            <Button size="icon-sm" variant="ghost" className="text-destructive-ink" aria-label={`Delete ${asset.name}`} onClick={() => onDelete(`Delete ${asset.name}?`, 'Its points go with it.', `/api/plant/assets/${asset.id}`)}>
              <Trash2 />
            </Button>
          </>
        )}
      </CardHeader>
      <CardContent>
        {points.length === 0 ? (
          <p className="text-sm text-muted-foreground">No points yet.</p>
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full text-sm">
              <thead className="text-start text-xs text-muted-foreground">
                <tr>
                  <th className="py-1 pe-3 text-start font-medium">Point</th>
                  <th className="py-1 pe-3 text-start font-medium">Now</th>
                  <th className="py-1 pe-3 text-start font-medium">Type</th>
                  <th className="py-1 pe-3 text-start font-medium">Source</th>
                  <th className="py-1 pe-3 text-start font-medium">Limits</th>
                  {may && <th className="sr-only">Delete</th>}
                </tr>
              </thead>
              <tbody>
                {points.map((p) => (
                  <tr key={p.id} className="border-t">
                    <td className="py-1.5 pe-3">
                      {p.name}
                      {p.unit && <span className="text-muted-foreground"> ({p.unit})</span>}
                    </td>
                    <td className="py-1.5 pe-3 tabular-nums">
                      {p.latest ? (
                        <span title={new Date(p.latest.at).toLocaleString()}>{shown(p)}</span>
                      ) : (
                        <span className="text-muted-foreground">—</span>
                      )}
                    </td>
                    <td className="py-1.5 pe-3">{p.dataType}</td>
                    <td className="py-1.5 pe-3">
                      {p.source}
                      {p.address && <code className="ms-1 text-xs text-muted-foreground">{p.address}</code>}
                    </td>
                    <td className="py-1.5 pe-3 text-muted-foreground">{limits(p)}</td>
                    {may && (
                      <td className="py-1.5 text-end">
                        <Button size="icon-sm" variant="ghost" className="text-destructive-ink" aria-label={`Delete ${p.name}`} onClick={() => onDelete(`Delete ${p.name}?`, 'The point goes; its asset stays.', `/api/plant/points/${p.id}`)}>
                          <Trash2 />
                        </Button>
                      </td>
                    )}
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </CardContent>
    </Card>
  )
}
