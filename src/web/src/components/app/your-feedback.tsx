import { useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { useLocation } from 'react-router'
import { SendFeedbackDialog } from '@/components/app/send-feedback'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardAction, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { api } from '@/lib/api'
import { when } from '@/lib/format'

interface Mine {
  id: string
  kind: 'idea' | 'problem' | 'praise' | 'other'
  text: string
  page: string | null
  status: 'new' | 'read' | 'done'
  answer: string | null
  answeredAt: string | null
  createdAt: string
}

const status: Record<Mine['status'], string> = { new: 'Sent', read: 'Read', done: 'Done' }

/** Account → Your feedback: what you sent about Arena, whether the admins read it, and their answers. */
export function YourFeedback() {
  const mine = useQuery({ queryKey: ['feedback', 'mine'], queryFn: () => api<Mine[]>('/api/feedback/mine') })
  const [open, setOpen] = useState(false)
  const location = useLocation()
  return (
    <Card id="feedback">
      <CardHeader>
        <CardTitle>Your feedback</CardTitle>
        <CardDescription>What you sent about Arena, and the admins&apos; answers (they come to your bell too).</CardDescription>
        <CardAction>
          <Button variant="outline" size="sm" onClick={() => setOpen(true)}>
            Send feedback
          </Button>
        </CardAction>
      </CardHeader>
      <CardContent>
        {mine.data?.length === 0 && <p className="text-sm text-muted-foreground">Nothing sent yet.</p>}
        <ul className="grid gap-3" aria-label="Your feedback">
          {mine.data?.map((f) => (
            <li key={f.id} className="grid gap-1.5 rounded-lg border px-3 py-2">
              <div className="flex items-center gap-2 text-xs text-muted-foreground">
                <Badge variant={f.status === 'new' ? 'outline' : 'secondary'}>{status[f.status]}</Badge>
                <span>{when(f.createdAt)}</span>
              </div>
              <p dir="auto" className="line-clamp-3 text-sm whitespace-pre-wrap">
                {f.text}
              </p>
              {f.answer && (
                <p className="rounded-md bg-muted/50 px-2.5 py-1.5 text-sm">
                  <span className="text-xs text-muted-foreground">Answer: </span>
                  {f.answer}
                </p>
              )}
            </li>
          ))}
        </ul>
      </CardContent>
      {open && <SendFeedbackDialog page={location.pathname} send={(body) => api('/api/feedback', { body })} onClose={() => setOpen(false)} />}
    </Card>
  )
}
