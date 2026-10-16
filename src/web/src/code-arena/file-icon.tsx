import { File, FileCode2, FileImage, FileJson, FileText, FileTerminal, Folder, FolderOpen, Settings2 } from 'lucide-react'
import { cn } from '@/lib/utils'
import { themedIcon, useIconTheme, type ThemedIcon } from './extensions'

const code = /\.(c|cc|cpp|cs|css|dart|fs|go|h|hpp|html?|java|jsx?|kt|less|lua|m|mjs|cjs|php|py|r|rb|rs|scala|scss|sql|svelte|swift|tsx?|vue|xml|xaml|csproj|props|targets|slnx?)$/i
const text = /\.(md|mdx|txt|rst|adoc|log|csv)$/i
const shell = /\.(sh|bash|zsh|fish|ps1|psm1|bat|cmd)$/i
const image = /\.(png|jpe?g|gif|webp|svg|ico|bmp|avif)$/i
const settings = /(^|\/)(\.[^/]*rc|\.env[^/]*|\.editorconfig|\.gitignore|\.gitattributes|dockerfile|makefile)$|\.(ya?ml|toml|ini|conf|cfg|lock)$/i

/** An icon of the file icon theme ("workbench.iconTheme"): its image (served by code-arena), or its font's character. */
function Themed({ icon, className }: { icon: ThemedIcon; className?: string }) {
  if (icon.kind === 'image') return <img src={icon.src} alt="" aria-hidden="true" draggable={false} className={cn('size-4 shrink-0 object-contain', className)} />
  return (
    <span
      aria-hidden="true"
      className={cn('grid size-4 shrink-0 place-items-center overflow-hidden leading-none', className)}
      style={{ fontFamily: `"${icon.family}"`, color: icon.color, fontSize: icon.size ?? '1rem' }}
    >
      {icon.character}
    </span>
  )
}

/** A file's icon: the icon theme's for it, else Code Arena's own by its kind (code, text, data, shell, image, settings). */
export function FileIcon({ path, className }: { path: string; className?: string }) {
  const theme = useIconTheme()
  const themed = theme ? themedIcon(theme, { kind: 'file', path }) : null
  if (themed) return <Themed icon={themed} className={className} />
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

/** A folder's icon, open or closed: the icon theme's for its name, else Code Arena's own. */
export function FolderIcon({ path, open, className }: { path: string; open?: boolean; className?: string }) {
  const theme = useIconTheme()
  const themed = theme ? themedIcon(theme, { kind: 'folder', path, open }) : null
  if (themed) return <Themed icon={themed} className={className} />
  const Icon = open ? FolderOpen : Folder
  return <Icon className={cn('size-4 shrink-0 text-primary/80', className)} aria-hidden="true" />
}
