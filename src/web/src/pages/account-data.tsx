import { useQuery } from '@tanstack/react-query'
import { Download } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { api } from '@/lib/api'

/** A person's own copy of their data, and how long their chats are kept. */
export function YourData() {
  const data = useQuery({ queryKey: ['account', 'data'], queryFn: () => api<{ retentionDays: number | null }>('/api/account/data') })
  const days = data.data?.retentionDays
  return (
    <Card>
      <CardHeader>
        <CardTitle>Your data</CardTitle>
        <CardDescription>
          A zip of your chats (each as JSON and as Markdown), your files, assistants, scheduled tasks and settings.
          {data.data && (days ? ` Chats are kept for ${days} days after their last message, then deleted.` : ' Chats are kept until you delete them.')}
        </CardDescription>
      </CardHeader>
      <CardContent>
        <Button variant="outline" asChild>
          <a href="/api/account/export" download>
            <Download /> Download your data
          </a>
        </Button>
      </CardContent>
    </Card>
  )
}
