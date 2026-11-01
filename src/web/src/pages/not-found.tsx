import { FileQuestion } from 'lucide-react'
import { useCallback, useState } from 'react'
import { Link } from 'react-router'
import { GameDialog } from '@/components/app/games'
import { useQuickClicks } from '@/components/app/games-kit'
import { Button } from '@/components/ui/button'
import { EmptyState } from '@/components/ui/empty-state'

export function NotFoundPage() {
  // Lost anyway: five quick clicks on the page's words open a game.
  const [playing, setPlaying] = useState(false)
  const play = useQuickClicks(5, useCallback(() => setPlaying(true), []))
  return (
    <div className="mx-auto max-w-lg py-16">
      <h1 className="sr-only">Page not found</h1>
      <EmptyState
        icon={FileQuestion}
        title="Page not found"
        action={
          <Button asChild>
            <Link to="/">Go home</Link>
          </Button>
        }
      >
        <button type="button" onClick={play} className="cursor-text text-inherit select-none">
          The address may be mistyped, or the page has moved.
        </button>
      </EmptyState>
      <GameDialog game={playing ? 'snake' : null} onClose={() => setPlaying(false)} />
    </div>
  )
}
