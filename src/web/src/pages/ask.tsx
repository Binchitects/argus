import { useQueryClient } from '@tanstack/react-query'
import { FileText, MessageSquareText, Puzzle } from 'lucide-react'
import { useState } from 'react'
import { useNavigate } from 'react-router'
import { PageHeader } from '@/components/app/page-header'
import { Alert } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { EmptyState } from '@/components/ui/empty-state'
import { Field } from '@/components/ui/field'
import { Textarea } from '@/components/ui/input'
import { api, errorMessage } from '@/lib/api'
import { clearQuote, quoteFile, readQuote, type Quote } from '@/lib/handoff'
import { streamChat, uploadFile } from './chat/api'

const suggestions = ['Summarize it.', 'What are the key points?', 'Explain it simply.']

/**
 * Where the browser extension hands over a page or a selection: the person asks about
 * it, and a new chat starts with it attached (as a file the model reads).
 */
export function AskPage() {
  const [quote] = useState<Quote | null>(readQuote)
  const [question, setQuestion] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const navigate = useNavigate()
  const queryClient = useQueryClient()

  if (!quote) {
    return (
      <>
        <PageHeader title="Ask about a page" />
        <EmptyState icon={Puzzle} title="Nothing to ask about yet">
          The browser extension opens this page with the page you are on, or what you selected on it.
        </EmptyState>
      </>
    )
  }

  const ask = async (text: string) => {
    setBusy(true)
    setError(null)
    try {
      const file = await uploadFile(quoteFile(quote), () => {})
      const { id } = await api<{ id: string }>('/api/chat/conversations', { body: {} })
      // The answer runs on the server: once it has the question, the chat page watches it.
      const watching = new AbortController()
      await streamChat(`/api/chat/conversations/${id}/messages`, { content: text, attachments: [file.id] }, (e) => e.type === 'question' && watching.abort(), watching.signal).catch(
        (e: unknown) => {
          if (!watching.signal.aborted) throw e
        },
      )
      clearQuote()
      void queryClient.invalidateQueries({ queryKey: ['chat', 'list'] })
      navigate(`/chat/${id}`)
    } catch (e) {
      setError(errorMessage(e, 'The question could not be sent. Try again.'))
      setBusy(false)
    }
  }

  return (
    <>
      <PageHeader title={quote.selection ? 'Ask about the selection' : 'Ask about this page'} description="A new chat starts with it attached; the model reads it as a file." />
      <Card className="max-w-3xl">
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <FileText className="size-4 text-muted-foreground" aria-hidden="true" />
            <span className="truncate">{quote.title}</span>
          </CardTitle>
          {quote.url && <CardDescription className="break-all">{quote.url}</CardDescription>}
        </CardHeader>
        <CardContent>
          <form
            className="grid gap-4"
            onSubmit={(e) => {
              e.preventDefault()
              void ask(question.trim() || suggestions[0])
            }}
          >
            <blockquote className="max-h-48 overflow-y-auto rounded-md border-l-4 bg-muted/50 px-3 py-2 text-sm whitespace-pre-wrap text-muted-foreground" aria-label="What you are asking about">
              {quote.text.length > 2000 ? `${quote.text.slice(0, 2000)}…` : quote.text}
            </blockquote>
            <p className="text-xs text-muted-foreground">{quote.text.length.toLocaleString()} characters</p>
            {error && <Alert variant="destructive">{error}</Alert>}
            <Field label="Your question">
              <Textarea value={question} onChange={(e) => setQuestion(e.target.value)} placeholder={suggestions[0]} autoFocus />
            </Field>
            <div className="flex flex-wrap gap-2">
              {suggestions.map((s) => (
                <Button key={s} type="button" variant="outline" size="sm" disabled={busy} onClick={() => setQuestion(s)}>
                  {s}
                </Button>
              ))}
            </div>
            <div>
              <Button type="submit" loading={busy}>
                <MessageSquareText /> Ask
              </Button>
            </div>
          </form>
        </CardContent>
      </Card>
    </>
  )
}
