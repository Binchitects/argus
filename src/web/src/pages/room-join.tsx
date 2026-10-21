import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Users } from 'lucide-react'
import { useNavigate, useParams } from 'react-router'
import { PageHeader } from '@/components/app/page-header'
import { Alert } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Skeleton } from '@/components/ui/skeleton'
import { api, errorMessage } from '@/lib/api'

interface Preview {
  room: string
  title: string
  owner: string
  role: 'member' | 'viewer'
  people: number
  inside: boolean
}

/** A room's invite link: what it leads to, and Join. */
export function RoomJoinPage() {
  const { token = '' } = useParams()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const preview = useQuery({ queryKey: ['rooms', 'join', token], queryFn: ({ signal }) => api<Preview>(`/api/rooms/join/${encodeURIComponent(token)}`, { signal }), retry: false })
  const join = useMutation({
    mutationFn: () => api<{ id: string }>(`/api/rooms/join/${encodeURIComponent(token)}`, { body: {} }),
    onSuccess: async (r) => {
      await queryClient.invalidateQueries({ queryKey: ['chat', 'list'] })
      navigate(`/chat/${r.id}`)
    },
  })
  return (
    <>
      <PageHeader title="Join a room" description="A room is a chat several people share." />
      {preview.error ? (
        <Alert variant="destructive">{errorMessage(preview.error)}</Alert>
      ) : !preview.data ? (
        <Skeleton className="h-40 max-w-lg" />
      ) : (
        <Card className="max-w-lg">
          <CardHeader>
            <CardTitle className="flex items-center gap-2">
              <Users className="size-5 text-primary-ink" aria-hidden="true" /> {preview.data.title}
            </CardTitle>
            <CardDescription>
              {preview.data.owner}&apos;s room, {preview.data.people} {preview.data.people === 1 ? 'person' : 'people'}. You join as a {preview.data.role}
              {preview.data.role === 'viewer' ? ': you read it, and do not write in it.' : ': you read it, write in it and ask the model.'}
            </CardDescription>
          </CardHeader>
          <CardContent className="grid gap-3">
            {join.error && <Alert variant="destructive">{errorMessage(join.error)}</Alert>}
            {preview.data.inside ? (
              <Button onClick={() => navigate(`/chat/${preview.data.room}`)}>You are in it: open it</Button>
            ) : (
              <Button onClick={() => join.mutate()} loading={join.isPending}>
                Join the room
              </Button>
            )}
          </CardContent>
        </Card>
      )}
    </>
  )
}
