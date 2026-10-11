import { useEffect, useState, type ComponentProps } from 'react'
import { cn } from '@/lib/utils'
import { runCommand } from './commands'
import { useEditor } from './editor-state'
import { quickPick } from './quick-pick'

function Item({ className, ...props }: ComponentProps<'button'>) {
  return (
    <button
      type="button"
      className={cn('flex h-full min-w-0 shrink-0 items-center gap-1 px-2 outline-none hover:bg-white/15 focus-visible:bg-white/20 focus-visible:ring-1 focus-visible:ring-white/70 focus-visible:ring-inset', className)}
      {...props}
    />
  )
}

/**
 * The file shown's indentation, line endings and language in the status bar, each a choice as in VS Code: indent with
 * spaces or tabs (or detect it, or convert the file), LF or CRLF, another language (or its settings).
 */
export function LanguageStatus() {
  const ed = useEditor()
  const { active } = ed
  const model = active?.kind === 'file' && active.status === 'ready' ? ed.activeModel() : null
  // The model's options and line endings change without React knowing: read again as they do.
  const [, setTick] = useState(0)
  useEffect(() => {
    if (!model) return
    const a = model.onDidChangeOptions?.(() => setTick((n) => n + 1))
    const b = model.onDidChangeContent(() => setTick((n) => n + 1))
    return () => {
      a?.dispose()
      b.dispose()
    }
  }, [model])
  if (!active || active.status !== 'ready') return null
  const options = model?.getOptions?.()
  const eol = model?.getEOL?.()
  const indentation = async () => {
    const picked = await quickPick({
      title: 'Indentation',
      placeholder: 'Select an action',
      items: [
        { id: 'editor.action.indentUsingSpaces', label: 'Indent Using Spaces' },
        { id: 'editor.action.indentUsingTabs', label: 'Indent Using Tabs' },
        { id: 'editor.action.detectIndentation', label: 'Detect Indentation from Content' },
        { id: 'editor.action.indentationToSpaces', label: 'Convert Indentation to Spaces' },
        { id: 'editor.action.indentationToTabs', label: 'Convert Indentation to Tabs' },
      ],
    })
    if (picked) ed.runAction(picked.id)
  }
  const lineEndings = async () => {
    const picked = await quickPick({
      title: 'End of line sequence',
      placeholder: 'Select an end of line sequence',
      items: [
        { id: 'LF', label: 'LF', description: '\\n (Linux, macOS)' },
        { id: 'CRLF', label: 'CRLF', description: '\\r\\n (Windows)' },
      ],
    })
    if (picked && model && !model.isDisposed()) {
      model.pushStackElement()
      model.pushEOL(picked.id === 'LF' ? 0 : 1)
      model.pushStackElement()
    }
  }
  return (
    <>
      {options && (
        <Item onClick={() => void indentation()} title="Select indentation" aria-label={`${options.insertSpaces ? 'Spaces' : 'Tab size'}: ${options.tabSize}. Change the indentation`}>
          {options.insertSpaces ? 'Spaces' : 'Tab Size'}: {options.tabSize}
        </Item>
      )}
      {eol && (
        <Item onClick={() => void lineEndings()} title="Select end of line sequence" aria-label={`Line endings ${eol === '\r\n' ? 'CRLF' : 'LF'}: change them`} className="hidden sm:flex">
          {eol === '\r\n' ? 'CRLF' : 'LF'}
        </Item>
      )}
      {active.language && (
        <Item onClick={() => runCommand('workbench.action.editor.changeLanguageMode')} title="Select language mode" aria-label={`Language ${active.language}: change it`} className="hidden sm:flex">
          {active.language}
        </Item>
      )}
    </>
  )
}
