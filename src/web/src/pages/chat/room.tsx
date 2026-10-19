import { useQueryClient } from '@tanstack/react-query'
import { Crown, LogOut, UserPlus, Users, X } from 'lucide-react'
import { useState } from 'react'
import { useNavigate } from 'react-router'
import { Segmented } from '@/components/app/segmented'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { useConfirm } from '@/components/ui/confirm'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle } from '@/components/ui/sheet'
import { toast } from '@/components/ui/toaster'
import { errorMessage } from '@/lib/api'
import { addToRoom, conversationQuery, createRoom, handOverRoom, removeFromRoom, removeGroupFromRoom, setRoomRole, updateRoom } from './api'
import { GroupChecklist, PeoplePicker, type Person } from './assistant-sharing'
import type { RoomBrief } from './types'

type Role = 'member' | 'viewer'

/** People and groups to add, with the role they get. */
function Invite({ people, setPeople, groups, setGroups, role, setRole }: { people: Person[]; setPeople: (p: Person[]) => void; groups: string[]; setGroups: (g: string[]) => void; role: Role; setRole: (r: Role) => void }) {
  return (
    <div className="grid gap-4">
      <Field label="People">
        <PeoplePicker chosen={people} onChange={setPeople} label="People to add" />
      </Field>
      <Field label="Groups" hint="Whoever is in a group now is in the room.">
        <GroupChecklist chosen={groups} onChange={setGroups} label="Groups to add" />
      </Field>
      <Segmented
        label="They may"
        value={role}
        onChange={setRole}
        options={[
          { value: 'member', label: 'Read and ask' },
          { value: 'viewer', label: 'Only read' },
        ]}
      />
    </div>
  )
}

/** A new room; or, with `from`, the person's chat made into one (what it said so far becomes the room's). */
export function NewRoomDialog({ open, onOpenChange, from }: { open: boolean; onOpenChange: (o: boolean) => void; from?: { id: string; title: string } }) {
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const [title, setTitle] = useState('')
  const [people, setPeople] = useState<Person[]>([])
  const [groups, setGroups] = useState<string[]>([])
  const [role, setRole] = useState<Role>('member')
  const [error, setError] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)
  const close = () => {
    onOpenChange(false)
    setTimeout(() => {
      setTitle('')
      setPeople([])
      setGroups([])
      setError(null)
    }, 200)
  }
  const make = async () => {
    setSaving(true)
    setError(null)
    try {
      const made = await createRoom({ title: title.trim() || undefined, userIds: people.map((p) => p.id), groupIds: groups, role, ...(from && { from: from.id }) })
      await queryClient.invalidateQueries({ queryKey: ['chat'] })
      close()
      navigate(`/chat/${made.id}`)
    } catch (e) {
      setError(errorMessage(e))
    } finally {
      setSaving(false)
    }
  }
  return (
    <Dialog open={open} onOpenChange={(o) => (o ? onOpenChange(true) : close())}>
      <DialogContent className="max-h-[90vh] overflow-y-auto">
        <DialogHeader>
          <DialogTitle>{from ? 'Make it a room' : 'New room'}</DialogTitle>
          <DialogDescription>
            {from
              ? 'The people you add read this chat from its start, and write in it. Whoever asks pays for the answer.'
              : 'A chat several people share. Each sees who wrote what; whoever asks pays for the answer, under their own rules.'}
          </DialogDescription>
        </DialogHeader>
        <div className="grid gap-4">
          {error && <Alert variant="destructive">{error}</Alert>}
          <Field label="Name">
            <Input value={title} onChange={(e) => setTitle(e.target.value)} placeholder={from?.title ?? 'e.g. Launch plan'} autoFocus />
          </Field>
          <Invite people={people} setPeople={setPeople} groups={groups} setGroups={setGroups} role={role} setRole={setRole} />
        </div>
        <DialogFooter>
          <Button variant="outline" onClick={close}>
            Cancel
          </Button>
          <Button loading={saving} onClick={make}>
            {from ? 'Make it a room' : 'Make room'}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

/** A room's people and settings: invite, change roles, take out, hand over, leave. */
export function RoomPanel({ chatId, room, me, open, onOpenChange, here }: { chatId: string; room: RoomBrief; me: string; open: boolean; onOpenChange: (o: boolean) => void; here: string[] }) {
  const queryClient = useQueryClient()
  const navigate = useNavigate()
  const confirm = useConfirm()
  const [people, setPeople] = useState<Person[]>([])
  const [groups, setGroups] = useState<string[]>([])
  const [role, setRole] = useState<Role>('member')
  const owner = room.role === 'owner'
  const mayInvite = owner || (room.role === 'member' && room.membersInvite)
  const refresh = () => queryClient.invalidateQueries({ queryKey: conversationQuery(chatId).queryKey })
  const act = async (work: Promise<unknown>, done?: string) => {
    try {
      await work
      if (done) toast.success(done)
      await refresh()
    } catch (e) {
      toast.error(errorMessage(e))
    }
  }
  return (
    <Sheet open={open} onOpenChange={onOpenChange}>
      <SheetContent className="w-full overflow-y-auto sm:max-w-md">
        <SheetHeader>
          <SheetTitle>People in this room</SheetTitle>
          <SheetDescription>Whoever asks pays for the answer, under their own rules. Answers here use no one's memory, code or knowledge.</SheetDescription>
        </SheetHeader>
        <div className="grid gap-6 px-4 pb-6">
          <ul className="grid gap-1" aria-label="People">
            {room.members.map((m) => (
              <li key={m.id} className="flex items-center gap-2 rounded-md px-2 py-1.5 text-sm hover:bg-accent">
                <span className={`size-2 shrink-0 rounded-full ${here.includes(m.id) ? 'bg-success' : 'bg-muted-foreground/30'}`} aria-label={here.includes(m.id) ? 'Here now' : 'Away'} />
                <span className="min-w-0 flex-1 truncate">
                  {m.name}
                  {m.id === me && <span className="text-muted-foreground"> (you)</span>}
                </span>
                {m.role === 'owner' ? (
                  <Badge variant="secondary">
                    <Crown className="size-3" /> Owner
                  </Badge>
                ) : owner ? (
                  <>
                    <select
                      aria-label={`${m.name}'s role`}
                      className="h-7 rounded-md border bg-card px-1 text-xs"
                      value={m.role}
                      onChange={(e) => void act(setRoomRole(chatId, m.id, e.target.value as Role))}
                    >
                      <option value="member">Reads and asks</option>
                      <option value="viewer">Only reads</option>
                    </select>
                    <Button
                      size="icon-sm"
                      variant="ghost"
                      className="size-7"
                      aria-label={`Hand the room to ${m.name}`}
                      title="Hand the room over"
                      onClick={async () => {
                        if (await confirm({ title: `Hand the room to ${m.name}?`, description: 'They become its owner; you stay in it as a member.', confirm: 'Hand over' }))
                          void act(handOverRoom(chatId, m.id), `${m.name} owns the room now`)
                      }}
                    >
                      <Crown />
                    </Button>
                    <Button
                      size="icon-sm"
                      variant="ghost"
                      className="size-7"
                      aria-label={`Take ${m.name} out`}
                      onClick={async () => {
                        if (await confirm({ title: `Take ${m.name} out of the room?`, description: 'What they wrote stays.', confirm: 'Take out', destructive: true }))
                          void act(removeFromRoom(chatId, m.id))
                      }}
                    >
                      <X />
                    </Button>
                  </>
                ) : (
                  <Badge variant="outline">{m.role === 'viewer' ? 'Reads' : 'Member'}</Badge>
                )}
              </li>
            ))}
          </ul>
          {room.groups.length > 0 && (
            <ul className="grid gap-1" aria-label="Groups">
              {room.groups.map((g) => (
                <li key={g.id} className="flex items-center gap-2 px-2 text-sm">
                  <Users className="size-4 text-muted-foreground" aria-hidden="true" />
                  <span className="min-w-0 flex-1 truncate">{g.name}</span>
                  <Badge variant="outline">{g.role === 'viewer' ? 'Reads' : 'Members'}</Badge>
                  {owner && (
                    <Button size="icon-sm" variant="ghost" className="size-7" aria-label={`Take the group ${g.name} out`} onClick={() => void act(removeGroupFromRoom(chatId, g.id))}>
                      <X />
                    </Button>
                  )}
                </li>
              ))}
            </ul>
          )}
          {mayInvite && (
            <section className="grid gap-3" aria-label="Invite">
              <h3 className="text-sm font-medium">Invite</h3>
              <Invite people={people} setPeople={setPeople} groups={groups} setGroups={setGroups} role={role} setRole={setRole} />
              <div>
                <Button
                  size="sm"
                  disabled={people.length === 0 && groups.length === 0}
                  onClick={() =>
                    void act(addToRoom(chatId, { userIds: people.map((p) => p.id), groupIds: groups, role }), 'Added').then(() => {
                      setPeople([])
                      setGroups([])
                    })
                  }
                >
                  <UserPlus /> Add
                </Button>
              </div>
            </section>
          )}
          {owner && (
            <section className="grid gap-3" aria-label="Settings">
              <h3 className="text-sm font-medium">Settings</h3>
              <Segmented
                label="The model answers"
                value={room.answerMode}
                onChange={(answerMode) => void act(updateRoom(chatId, { answerMode }))}
                options={[
                  { value: 'every', label: 'Every message' },
                  { value: 'mention', label: 'Only when @Arena is named' },
                ]}
              />
              <div className="flex items-center gap-2 text-sm">
                <Checkbox id="room-members-invite" checked={room.membersInvite} onCheckedChange={(on) => void act(updateRoom(chatId, { membersInvite: on === true }))} />
                <label htmlFor="room-members-invite">Members may invite others</label>
              </div>
            </section>
          )}
          {!owner && (
            <div>
              <Button
                variant="outline"
                onClick={async () => {
                  if (!(await confirm({ title: 'Leave this room?', description: 'You will not see it any more; what you wrote stays.', confirm: 'Leave', destructive: true }))) return
                  try {
                    await removeFromRoom(chatId, me)
                    await queryClient.invalidateQueries({ queryKey: ['chat', 'list'] })
                    navigate('/chat')
                  } catch (e) {
                    toast.error(errorMessage(e))
                  }
                }}
              >
                <LogOut /> Leave the room
              </Button>
            </div>
          )}
        </div>
      </SheetContent>
    </Sheet>
  )
}
