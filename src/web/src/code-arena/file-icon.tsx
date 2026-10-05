import { File, FileCode2, FileImage, FileJson, FileText, FileTerminal, Settings2 } from 'lucide-react'
import { cn } from '@/lib/utils'

const code = /\.(c|cc|cpp|cs|css|dart|fs|go|h|hpp|html?|java|jsx?|kt|less|lua|m|mjs|cjs|php|py|r|rb|rs|scala|scss|sql|svelte|swift|tsx?|vue|xml|xaml|csproj|props|targets|slnx?)$/i
const text = /\.(md|mdx|txt|rst|adoc|log|csv)$/i
const shell = /\.(sh|bash|zsh|fish|ps1|psm1|bat|cmd)$/i
const image = /\.(png|jpe?g|gif|webp|svg|ico|bmp|avif)$/i
const settings = /(^|\/)(\.[^/]*rc|\.env[^/]*|\.editorconfig|\.gitignore|\.gitattributes|dockerfile|makefile)$|\.(ya?ml|toml|ini|conf|cfg|lock)$/i

/** A file's icon by its kind: code, text, data, shell, image, settings. */
export function FileIcon({ path, className }: { path: string; className?: string }) {
  const name = path.toLowerCase()
  const Icon = /\.(json|jsonc|json5)$/i.test(name)
    ? FileJson
    : code.test(name)
      ? FileCode2
      : text.test(name)
        ? FileText
        : shell.test(name)
          ? FileTerminal
          : image.test(name)
            ? FileImage
            : settings.test(name)
              ? Settings2
              : File
  return <Icon className={cn('size-4 shrink-0 text-muted-foreground', className)} aria-hidden="true" />
}
