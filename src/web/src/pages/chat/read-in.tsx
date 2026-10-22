import { useQuery } from '@tanstack/react-query'
import { Languages } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { DropdownMenu, DropdownMenuContent, DropdownMenuLabel, DropdownMenuRadioGroup, DropdownMenuRadioItem, DropdownMenuSeparator, DropdownMenuTrigger } from '@/components/ui/dropdown-menu'
import { roomLanguagesQuery } from './room-language'

/** The room's Read in menu: each message as written, or in a language of the reader's. */
export function ReadInMenu({ value, onChange }: { value: string; onChange: (code: string) => void }) {
  const languages = useQuery(roomLanguagesQuery)
  const chosen = languages.data?.find((l) => l.code === value)
  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button variant={value ? 'secondary' : 'ghost'} size="sm" className="h-8 gap-1.5" aria-label={chosen ? `Reading in ${chosen.name}` : 'Read the room in your language'}>
          <Languages /> {chosen && <span className="max-w-24 truncate">{chosen.own}</span>}
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end" className="max-h-80 overflow-y-auto">
        <DropdownMenuLabel>Read the room in</DropdownMenuLabel>
        <DropdownMenuRadioGroup value={value} onValueChange={onChange}>
          <DropdownMenuRadioItem value="">Each message as written</DropdownMenuRadioItem>
          <DropdownMenuSeparator />
          {(languages.data ?? []).map((l) => (
            <DropdownMenuRadioItem key={l.code} value={l.code}>
              <span dir="auto">{l.own}</span>
              {l.own !== l.name && <span className="ml-auto pl-3 text-xs text-muted-foreground">{l.name}</span>}
            </DropdownMenuRadioItem>
          ))}
        </DropdownMenuRadioGroup>
      </DropdownMenuContent>
    </DropdownMenu>
  )
}

/** Under a translated message: that it is, and the original a click away (and back). */
export function TranslatedNote({ original, onToggle }: { original: boolean; onToggle: () => void }) {
  return (
    <button type="button" onClick={onToggle} className="px-1 text-xs text-muted-foreground underline-offset-2 hover:underline">
      {original ? 'Show the translation' : 'Translated · Show the original'}
    </button>
  )
}
