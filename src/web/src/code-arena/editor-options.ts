import type * as Monaco from 'monaco-editor'
import type { UserFileName } from './ide-api'

/** The person's own settings files are "user:settings.json" and "user:keybindings.json" in the editor. */
export const isUserFile = (path: string) => path.startsWith('user:')
export const userFile = (name: UserFileName) => `user:${name}`

/** The editor's options from the settings (for the file's language). */
export function editorOptionsOf(v: Record<string, unknown>, wordWrap?: string): Monaco.editor.IEditorOptions & Monaco.editor.IGlobalEditorOptions {
  const lineHeight = v['editor.lineHeight'] as number
  return {
    fontSize: v['editor.fontSize'] as number,
    fontFamily: v['editor.fontFamily'] as string,
    fontWeight: v['editor.fontWeight'] as string,
    fontLigatures: v['editor.fontLigatures'] as boolean,
    lineHeight,
    letterSpacing: v['editor.letterSpacing'] as number,
    wordWrap: (wordWrap ?? v['editor.wordWrap']) as 'off' | 'on' | 'wordWrapColumn' | 'bounded',
    wordWrapColumn: v['editor.wordWrapColumn'] as number,
    rulers: v['editor.rulers'] as number[],
    lineNumbers: v['editor.lineNumbers'] as 'on' | 'off' | 'relative' | 'interval',
    renderWhitespace: v['editor.renderWhitespace'] as 'none' | 'boundary' | 'selection' | 'trailing' | 'all',
    renderControlCharacters: v['editor.renderControlCharacters'] as boolean,
    renderLineHighlight: v['editor.renderLineHighlight'] as 'none' | 'gutter' | 'line' | 'all',
    cursorStyle: v['editor.cursorStyle'] as 'line' | 'block' | 'underline' | 'line-thin' | 'block-outline' | 'underline-thin',
    cursorBlinking: v['editor.cursorBlinking'] as 'blink' | 'smooth' | 'phase' | 'expand' | 'solid',
    cursorSmoothCaretAnimation: v['editor.cursorSmoothCaretAnimation'] as 'off' | 'explicit' | 'on',
    minimap: { enabled: v['editor.minimap.enabled'] as boolean, renderCharacters: v['editor.minimap.renderCharacters'] as boolean, side: v['editor.minimap.side'] as 'left' | 'right' },
    stickyScroll: { enabled: v['editor.stickyScroll.enabled'] as boolean },
    bracketPairColorization: { enabled: v['editor.bracketPairColorization.enabled'] as boolean },
    guides: { bracketPairs: v['editor.guides.bracketPairs'] as boolean, indentation: v['editor.guides.indentation'] as boolean },
    folding: v['editor.folding'] as boolean,
    glyphMargin: v['editor.glyphMargin'] as boolean,
    smoothScrolling: v['editor.smoothScrolling'] as boolean,
    scrollBeyondLastLine: v['editor.scrollBeyondLastLine'] as boolean,
    mouseWheelZoom: v['editor.mouseWheelZoom'] as boolean,
    multiCursorModifier: v['editor.multiCursorModifier'] as 'ctrlCmd' | 'alt',
    autoClosingBrackets: v['editor.autoClosingBrackets'] as 'always' | 'languageDefined' | 'beforeWhitespace' | 'never',
    autoClosingQuotes: v['editor.autoClosingQuotes'] as 'always' | 'languageDefined' | 'beforeWhitespace' | 'never',
    autoIndent: v['editor.autoIndent'] as 'none' | 'keep' | 'brackets' | 'advanced' | 'full',
    linkedEditing: v['editor.linkedEditing'] as boolean,
    matchBrackets: v['editor.matchBrackets'] as 'never' | 'near' | 'always',
    occurrencesHighlight: v['editor.occurrencesHighlight'] as 'off' | 'singleFile',
    selectionHighlight: v['editor.selectionHighlight'] as boolean,
    emptySelectionClipboard: v['editor.emptySelectionClipboard'] as boolean,
    dragAndDrop: v['editor.dragAndDrop'] as boolean,
    hover: { enabled: v['editor.hover.enabled'] ? 'on' : 'off', delay: v['editor.hover.delay'] as number },
    parameterHints: { enabled: v['editor.parameterHints.enabled'] as boolean },
    quickSuggestions: v['editor.quickSuggestions'] as boolean,
    suggestOnTriggerCharacters: v['editor.suggestOnTriggerCharacters'] as boolean,
    acceptSuggestionOnEnter: v['editor.acceptSuggestionOnEnter'] as 'on' | 'smart' | 'off',
    tabCompletion: v['editor.tabCompletion'] as 'on' | 'off' | 'onlySnippets',
    wordBasedSuggestions: v['editor.wordBasedSuggestions'] as 'off' | 'currentDocument' | 'matchingDocuments' | 'allDocuments',
    snippetSuggestions: v['editor.snippetSuggestions'] as 'top' | 'bottom' | 'inline' | 'none',
    inlineSuggest: { enabled: v['editor.inlineSuggest.enabled'] as boolean },
    unicodeHighlight: { ambiguousCharacters: v['editor.unicodeHighlight.ambiguousCharacters'] as boolean },
    copyWithSyntaxHighlighting: v['editor.copyWithSyntaxHighlighting'] as boolean,
    accessibilitySupport: v['editor.accessibilitySupport'] as 'auto' | 'on' | 'off',
    formatOnPaste: v['editor.formatOnPaste'] as boolean,
  }
}

/** A compare's options from the settings. */
export function diffOptionsOf(v: Record<string, unknown>): Monaco.editor.IDiffEditorOptions & Monaco.editor.IGlobalEditorOptions {
  return {
    ...editorOptionsOf(v),
    renderSideBySide: v['diffEditor.renderSideBySide'] as boolean,
    ignoreTrimWhitespace: v['diffEditor.ignoreTrimWhitespace'] as boolean,
    hideUnchangedRegions: { enabled: v['diffEditor.hideUnchangedRegions.enabled'] as boolean },
    renderIndicators: v['diffEditor.renderIndicators'] as boolean,
    diffWordWrap: v['diffEditor.wordWrap'] as 'off' | 'on' | 'inherit',
    maxComputationTime: v['diffEditor.maxComputationTime'] as number,
  }
}

