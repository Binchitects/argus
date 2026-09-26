import { Inbox, Mail, MoreHorizontal, Plus, Trash2 } from 'lucide-react'
import { useState, type ReactNode } from 'react'
import { PageHeader } from '@/components/app/page-header'
import { Alert } from '@/components/ui/alert'
import { Avatar } from '@/components/ui/avatar'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardAction, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Checkbox } from '@/components/ui/checkbox'
import { useConfirm } from '@/components/ui/confirm'
import { DataTable, SortHeader, selectColumn, type ColumnDef } from '@/components/ui/data-table'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle, DialogTrigger } from '@/components/ui/dialog'
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuSeparator, DropdownMenuTrigger } from '@/components/ui/dropdown-menu'
import { EmptyState } from '@/components/ui/empty-state'
import { Field } from '@/components/ui/field'
import { Input, Textarea } from '@/components/ui/input'
import { Kbd } from '@/components/ui/kbd'
import { Label } from '@/components/ui/label'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle, SheetTrigger } from '@/components/ui/sheet'
import { Skeleton } from '@/components/ui/skeleton'
import { Switch } from '@/components/ui/switch'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { toast } from '@/components/ui/toaster'
import { Tooltip } from '@/components/ui/tooltip'
import { money } from '@/lib/format'

interface Row {
  name: string
  email: string
  role: 'Admin' | 'Member'
  spend: number
}

const rows: Row[] = [
  { name: 'Ada Lovelace', email: 'ada@example.test', role: 'Admin', spend: 12.4 },
  { name: 'Grace Hopper', email: 'grace@example.test', role: 'Member', spend: 3.05 },
  { name: 'Alan Turing', email: 'alan@example.test', role: 'Member', spend: 0.004 },
  { name: 'Katherine Johnson', email: 'katherine@example.test', role: 'Member', spend: 27.9 },
]

const columns: ColumnDef<Row>[] = [
  selectColumn<Row>(),
  {
    accessorKey: 'name',
    header: 'Name',
    cell: ({ row }) => (
      <div className="flex items-center gap-2.5">
        <Avatar name={row.original.name} className="size-7" />
        <div>
          <p className="font-medium">{row.original.name}</p>
          <p className="text-xs text-muted-foreground">{row.original.email}</p>
        </div>
      </div>
    ),
  },
  { accessorKey: 'role', header: 'Role', cell: ({ getValue }) => <Badge variant={getValue() === 'Admin' ? 'default' : 'secondary'}>{getValue<string>()}</Badge> },
  { accessorKey: 'spend', header: ({ column }) => <SortHeader column={column} title="Spend" />, cell: ({ getValue }) => <span className="tabular-nums">{money(getValue<number>())}</span> },
]

function Section({ title, children }: { title: string; children: ReactNode }) {
  return (
    <Card>
      <CardHeader>
        <CardTitle>{title}</CardTitle>
      </CardHeader>
      <CardContent className="flex flex-wrap items-start gap-3">{children}</CardContent>
    </Card>
  )
}

/** Every component in every state: for reviewing the look and for the accessibility and screenshot tests. */
export function DesignPage() {
  const confirm = useConfirm()
  const [on, setOn] = useState(true)
  return (
    <>
      <PageHeader title="Design system" description="The components this web is built from, in their states." actions={<Button size="sm"><Plus /> Primary action</Button>} />
      <div className="grid gap-6">
        <Section title="Buttons">
          <Button>Default</Button>
          <Button variant="secondary">Secondary</Button>
          <Button variant="outline">Outline</Button>
          <Button variant="ghost">Ghost</Button>
          <Button variant="destructive">
            <Trash2 /> Delete
          </Button>
          <Button variant="link">Link</Button>
          <Button loading>Saving</Button>
          <Button disabled>Disabled</Button>
          <Tooltip content="More actions">
            <Button variant="outline" size="icon" aria-label="More actions">
              <MoreHorizontal />
            </Button>
          </Tooltip>
        </Section>
        <Section title="Badges and keys">
          <Badge>Default</Badge>
          <Badge variant="secondary">Secondary</Badge>
          <Badge variant="outline">Outline</Badge>
          <Badge variant="success">Healthy</Badge>
          <Badge variant="warning">Degraded</Badge>
          <Badge variant="destructive">Down</Badge>
          <Kbd>Ctrl K</Kbd>
        </Section>
        <Section title="Form controls">
          <div className="grid w-full gap-4 sm:grid-cols-2">
            <Field label="Email" hint="We never share it.">
              <Input type="email" placeholder="you@example.com" />
            </Field>
            <Field label="Name" error="Enter a name.">
              <Input />
            </Field>
            <Field label="Role">
              <Select defaultValue="member">
                <SelectTrigger>
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value="member">Member</SelectItem>
                  <SelectItem value="admin">Admin</SelectItem>
                </SelectContent>
              </Select>
            </Field>
            <Field label="Notes">
              <Textarea placeholder="Anything else" />
            </Field>
            <Label className="font-normal">
              <Checkbox defaultChecked /> Send a welcome email
            </Label>
            <Label className="font-normal">
              <Switch checked={on} onCheckedChange={setOn} /> Two-factor required
            </Label>
          </div>
        </Section>
        <Section title="Alerts">
          <div className="grid w-full gap-3">
            <Alert title="Heads up">The index is rebuilding; answers may miss new code for a few minutes.</Alert>
            <Alert variant="success">Saved. It applies at once.</Alert>
            <Alert variant="warning" title="Needs a restart">Run the command below to apply it.</Alert>
            <Alert variant="destructive">The gateway is not reachable.</Alert>
          </div>
        </Section>
        <Section title="Overlays">
          <Dialog>
            <DialogTrigger asChild>
              <Button variant="outline">Open dialog</Button>
            </DialogTrigger>
            <DialogContent>
              <DialogHeader>
                <DialogTitle>Invite someone</DialogTitle>
                <DialogDescription>They get an email with a link to set their password.</DialogDescription>
              </DialogHeader>
              <Field label="Email">
                <Input type="email" />
              </Field>
              <DialogFooter>
                <Button>Send invite</Button>
              </DialogFooter>
            </DialogContent>
          </Dialog>
          <Sheet>
            <SheetTrigger asChild>
              <Button variant="outline">Open panel</Button>
            </SheetTrigger>
            <SheetContent>
              <SheetHeader>
                <SheetTitle>Ada Lovelace</SheetTitle>
                <SheetDescription>ada@example.test</SheetDescription>
              </SheetHeader>
              <div className="px-5 text-sm text-muted-foreground">Details go here.</div>
            </SheetContent>
          </Sheet>
          <DropdownMenu>
            <DropdownMenuTrigger asChild>
              <Button variant="outline">Open menu</Button>
            </DropdownMenuTrigger>
            <DropdownMenuContent>
              <DropdownMenuItem>
                <Mail /> Send email
              </DropdownMenuItem>
              <DropdownMenuSeparator />
              <DropdownMenuItem variant="destructive">
                <Trash2 /> Delete
              </DropdownMenuItem>
            </DropdownMenuContent>
          </DropdownMenu>
          <Button variant="outline" onClick={() => toast.success('Saved', { description: 'It applies at once.' })}>
            Show a toast
          </Button>
          <Button
            variant="outline"
            onClick={async () => toast(String(await confirm({ title: 'Delete this chat?', description: 'It cannot be undone.', confirm: 'Delete', destructive: true })))}
          >
            Ask to confirm
          </Button>
        </Section>
        <Card>
          <CardHeader>
            <CardTitle>Tabs</CardTitle>
            <CardDescription>For views of the same thing.</CardDescription>
          </CardHeader>
          <CardContent>
            <Tabs defaultValue="everyone">
              <TabsList>
                <TabsTrigger value="everyone">Everyone</TabsTrigger>
                <TabsTrigger value="mine">Mine</TabsTrigger>
              </TabsList>
              <TabsContent value="everyone">Everyone's usage.</TabsContent>
              <TabsContent value="mine">Your usage.</TabsContent>
            </Tabs>
          </CardContent>
        </Card>
        <Card>
          <CardHeader>
            <CardTitle>Data table</CardTitle>
            <CardDescription>Search, sort, select, hide columns.</CardDescription>
            <CardAction>
              <Badge variant="outline">{rows.length} rows</Badge>
            </CardAction>
          </CardHeader>
          <CardContent>
            <DataTable
              columns={columns}
              data={rows}
              noun="people"
              getRowId={(r) => r.email}
              bulk={(sel) => (
                <Button size="sm" variant="outline" onClick={() => toast(`${sel.length} selected`)}>
                  Export selected
                </Button>
              )}
            />
          </CardContent>
        </Card>
        <Section title="Loading and empty">
          <div className="grid w-full gap-3 sm:grid-cols-2">
            <div className="grid gap-2">
              <Skeleton className="h-5 w-40" />
              <Skeleton className="h-4 w-64" />
              <Skeleton className="h-24 w-full" />
            </div>
            <EmptyState icon={Inbox} title="No chats yet" action={<Button size="sm">New chat</Button>}>
              Ask anything; your chats are kept here.
            </EmptyState>
          </div>
        </Section>
      </div>
    </>
  )
}
