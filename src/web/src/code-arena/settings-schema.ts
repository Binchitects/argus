// The settings Code Arena reads from settings.json, under VS Code's names, with VS Code's defaults (where Code Arena's
// differ, the description says so). One list gives the defaults, the JSON schema the settings files are checked with,
// and the rows of the Settings editor.

export type SettingType = 'boolean' | 'number' | 'integer' | 'string' | 'array' | 'object'

export interface SettingDef {
  key: string
  type: SettingType
  default: unknown
  description: string
  category: string
  enum?: readonly string[]
  enumDescriptions?: readonly string[]
  minimum?: number
  maximum?: number
  /** An array's elements. */
  items?: SettingType
  /**
   * language: may be set for one language ("[python]": {…}); application: the person's alone (a project's settings.json
   * cannot set it, as VS Code's cannot); window: either.
   */
  scope?: 'language' | 'application' | 'window'
}

const lang = 'language' as const

/** The themes offered, by VS Code's names for their kind; light or dark, and Monaco's theme. */
export const colorThemes: { name: string; kind: 'light' | 'dark'; monaco: string }[] = [
  { name: 'Code Arena Dark', kind: 'dark', monaco: 'arena-dark' },
  { name: 'Code Arena Light', kind: 'light', monaco: 'arena-light' },
  { name: 'Dark (Visual Studio)', kind: 'dark', monaco: 'vs-dark' },
  { name: 'Light (Visual Studio)', kind: 'light', monaco: 'vs' },
  { name: 'High Contrast', kind: 'dark', monaco: 'hc-black' },
  { name: 'High Contrast Light', kind: 'light', monaco: 'hc-light' },
]

/** A theme by its name; VS Code's own names (Default Dark Modern, Light+…) as the nearest of these. */
export function themeNamed(name: string | undefined) {
  const exact = colorThemes.find((t) => t.name === name)
  if (exact) return exact
  const n = (name ?? '').toLowerCase()
  if (n.includes('high contrast')) return colorThemes.find((t) => t.name === (n.includes('light') ? 'High Contrast Light' : 'High Contrast'))!
  return colorThemes.find((t) => t.name === (n.includes('light') ? 'Code Arena Light' : 'Code Arena Dark'))!
}

const themeNames = colorThemes.map((t) => t.name)

export const settingDefs: SettingDef[] = [
  // Text Editor
  { key: 'editor.fontSize', type: 'number', default: 13, minimum: 6, maximum: 100, description: 'The font size in pixels. (VS Code: 14.)', category: 'Text Editor', scope: lang },
  { key: 'editor.fontFamily', type: 'string', default: "'JetBrains Mono Variable', ui-monospace, 'SFMono-Regular', monospace", description: 'The font family, as CSS names it.', category: 'Text Editor', scope: lang },
  { key: 'editor.fontWeight', type: 'string', default: 'normal', description: 'The font weight: normal, bold, or a number from 1 to 1000.', category: 'Text Editor', scope: lang },
  { key: 'editor.fontLigatures', type: 'boolean', default: true, description: 'Joins characters such as => into ligatures, in fonts that have them. (VS Code: off.)', category: 'Text Editor', scope: lang },
  { key: 'editor.lineHeight', type: 'number', default: 20, minimum: 0, maximum: 150, description: 'The line height in pixels; from 1 to 8, a multiple of the font size; 0 works it out from the font size.', category: 'Text Editor', scope: lang },
  { key: 'editor.letterSpacing', type: 'number', default: 0, description: 'The letter spacing in pixels.', category: 'Text Editor', scope: lang },
  { key: 'editor.tabSize', type: 'integer', default: 4, minimum: 1, maximum: 32, description: 'The number of spaces a tab equals. Overridden by the file\'s own indentation when "editor.detectIndentation" is on.', category: 'Text Editor', scope: lang },
  { key: 'editor.insertSpaces', type: 'boolean', default: true, description: 'Inserts spaces when Tab is pressed. Overridden by the file\'s own indentation when "editor.detectIndentation" is on.', category: 'Text Editor', scope: lang },
  { key: 'editor.detectIndentation', type: 'boolean', default: true, description: 'Works out "editor.tabSize" and "editor.insertSpaces" from the file as it opens.', category: 'Text Editor', scope: lang },
  { key: 'editor.trimAutoWhitespace', type: 'boolean', default: true, description: 'Removes the whitespace the editor inserted at a line\'s end when nothing follows it.', category: 'Text Editor', scope: lang },
  { key: 'editor.wordWrap', type: 'string', default: 'off', enum: ['off', 'on', 'wordWrapColumn', 'bounded'], enumDescriptions: ['Lines never wrap.', 'Lines wrap at the editor\'s width.', 'Lines wrap at "editor.wordWrapColumn".', 'Lines wrap at the smaller of the editor\'s width and "editor.wordWrapColumn".'], description: 'How lines wrap. Alt+Z toggles it for the editor.', category: 'Text Editor', scope: lang },
  { key: 'editor.wordWrapColumn', type: 'integer', default: 80, minimum: 1, description: 'The column lines wrap at with "editor.wordWrap" wordWrapColumn or bounded.', category: 'Text Editor', scope: lang },
  { key: 'editor.rulers', type: 'array', items: 'number', default: [], description: 'Vertical rulers at these columns, e.g. [80, 120].', category: 'Text Editor', scope: lang },
  { key: 'editor.lineNumbers', type: 'string', default: 'on', enum: ['off', 'on', 'relative', 'interval'], description: 'How line numbers show.', category: 'Text Editor', scope: lang },
  { key: 'editor.renderWhitespace', type: 'string', default: 'selection', enum: ['none', 'boundary', 'selection', 'trailing', 'all'], description: 'Where whitespace characters show.', category: 'Text Editor', scope: lang },
  { key: 'editor.renderControlCharacters', type: 'boolean', default: true, description: 'Shows control characters.', category: 'Text Editor', scope: lang },
  { key: 'editor.renderLineHighlight', type: 'string', default: 'line', enum: ['none', 'gutter', 'line', 'all'], description: 'How the current line is highlighted.', category: 'Text Editor', scope: lang },
  { key: 'editor.cursorStyle', type: 'string', default: 'line', enum: ['line', 'block', 'underline', 'line-thin', 'block-outline', 'underline-thin'], description: 'The cursor\'s shape.', category: 'Text Editor', scope: lang },
  { key: 'editor.cursorBlinking', type: 'string', default: 'blink', enum: ['blink', 'smooth', 'phase', 'expand', 'solid'], description: 'How the cursor blinks.', category: 'Text Editor', scope: lang },
  { key: 'editor.cursorSmoothCaretAnimation', type: 'string', default: 'off', enum: ['off', 'explicit', 'on'], description: 'Whether the cursor glides to where it goes.', category: 'Text Editor', scope: lang },
  { key: 'editor.minimap.enabled', type: 'boolean', default: true, description: 'Shows the minimap.', category: 'Text Editor', scope: lang },
  { key: 'editor.minimap.renderCharacters', type: 'boolean', default: false, description: 'Draws the characters in the minimap, not blocks. (VS Code: on.)', category: 'Text Editor', scope: lang },
  { key: 'editor.minimap.side', type: 'string', default: 'right', enum: ['left', 'right'], description: 'Which side the minimap is on.', category: 'Text Editor', scope: lang },
  { key: 'editor.stickyScroll.enabled', type: 'boolean', default: true, description: 'Keeps the scopes the top line is in (classes, functions) at the top while scrolling.', category: 'Text Editor', scope: lang },
  { key: 'editor.bracketPairColorization.enabled', type: 'boolean', default: true, description: 'Colours matching brackets alike.', category: 'Text Editor', scope: lang },
  { key: 'editor.guides.bracketPairs', type: 'boolean', default: true, description: 'Draws guides between matching brackets. (VS Code: off.)', category: 'Text Editor', scope: lang },
  { key: 'editor.guides.indentation', type: 'boolean', default: true, description: 'Draws indentation guides.', category: 'Text Editor', scope: lang },
  { key: 'editor.folding', type: 'boolean', default: true, description: 'Lets code be folded.', category: 'Text Editor', scope: lang },
  { key: 'editor.glyphMargin', type: 'boolean', default: true, description: 'Shows the margin breakpoints are set in.', category: 'Text Editor', scope: lang },
  { key: 'editor.smoothScrolling', type: 'boolean', default: true, description: 'Scrolls with an animation. (VS Code: off.)', category: 'Text Editor', scope: lang },
  { key: 'editor.scrollBeyondLastLine', type: 'boolean', default: false, description: 'Scrolls past the last line. (VS Code: on.)', category: 'Text Editor', scope: lang },
  { key: 'editor.mouseWheelZoom', type: 'boolean', default: false, description: 'Ctrl and the mouse wheel change the font size.', category: 'Text Editor', scope: lang },
  { key: 'editor.multiCursorModifier', type: 'string', default: 'alt', enum: ['ctrlCmd', 'alt'], description: 'The key held to add a cursor with the mouse.', category: 'Text Editor', scope: lang },
  { key: 'editor.autoClosingBrackets', type: 'string', default: 'languageDefined', enum: ['always', 'languageDefined', 'beforeWhitespace', 'never'], description: 'Closes a bracket as it is opened.', category: 'Text Editor', scope: lang },
  { key: 'editor.autoClosingQuotes', type: 'string', default: 'languageDefined', enum: ['always', 'languageDefined', 'beforeWhitespace', 'never'], description: 'Closes a quote as it is opened.', category: 'Text Editor', scope: lang },
  { key: 'editor.autoIndent', type: 'string', default: 'full', enum: ['none', 'keep', 'brackets', 'advanced', 'full'], description: 'How lines are indented as they are typed, pasted or moved.', category: 'Text Editor', scope: lang },
  { key: 'editor.linkedEditing', type: 'boolean', default: false, description: 'Renames an HTML tag\'s closing tag as its opening tag is edited.', category: 'Text Editor', scope: lang },
  { key: 'editor.matchBrackets', type: 'string', default: 'always', enum: ['never', 'near', 'always'], description: 'Highlights the matching bracket.', category: 'Text Editor', scope: lang },
  { key: 'editor.occurrencesHighlight', type: 'string', default: 'singleFile', enum: ['off', 'singleFile'], description: 'Highlights the other occurrences of the symbol at the cursor.', category: 'Text Editor', scope: lang },
  { key: 'editor.selectionHighlight', type: 'boolean', default: true, description: 'Highlights text like the selection.', category: 'Text Editor', scope: lang },
  { key: 'editor.emptySelectionClipboard', type: 'boolean', default: true, description: 'Copying with nothing selected copies the line.', category: 'Text Editor', scope: lang },
  { key: 'editor.dragAndDrop', type: 'boolean', default: true, description: 'Lets a selection be moved by dragging it.', category: 'Text Editor', scope: lang },
  { key: 'editor.hover.enabled', type: 'boolean', default: true, description: 'Shows hovers.', category: 'Text Editor', scope: lang },
  { key: 'editor.hover.delay', type: 'integer', default: 300, minimum: 0, description: 'Milliseconds before a hover shows.', category: 'Text Editor', scope: lang },
  { key: 'editor.parameterHints.enabled', type: 'boolean', default: true, description: 'Shows a function\'s parameters as it is called.', category: 'Text Editor', scope: lang },
  { key: 'editor.quickSuggestions', type: 'boolean', default: true, description: 'Suggests as you type.', category: 'Text Editor', scope: lang },
  { key: 'editor.suggestOnTriggerCharacters', type: 'boolean', default: true, description: 'Suggests after characters such as ".".', category: 'Text Editor', scope: lang },
  { key: 'editor.acceptSuggestionOnEnter', type: 'string', default: 'on', enum: ['on', 'smart', 'off'], description: 'Whether Enter takes a suggestion (Tab always does).', category: 'Text Editor', scope: lang },
  { key: 'editor.tabCompletion', type: 'string', default: 'off', enum: ['on', 'off', 'onlySnippets'], description: 'Tab completes the word typed.', category: 'Text Editor', scope: lang },
  { key: 'editor.wordBasedSuggestions', type: 'string', default: 'matchingDocuments', enum: ['off', 'currentDocument', 'matchingDocuments', 'allDocuments'], description: 'Where the words suggested come from.', category: 'Text Editor', scope: lang },
  { key: 'editor.snippetSuggestions', type: 'string', default: 'inline', enum: ['top', 'bottom', 'inline', 'none'], description: 'Where snippets are among the suggestions.', category: 'Text Editor', scope: lang },
  { key: 'editor.inlineSuggest.enabled', type: 'boolean', default: true, description: 'Shows inline suggestions (Code Arena\'s completion) as grey text.', category: 'Text Editor', scope: lang },
  { key: 'editor.unicodeHighlight.ambiguousCharacters', type: 'boolean', default: true, description: 'Highlights characters that look like others.', category: 'Text Editor', scope: lang },
  { key: 'editor.copyWithSyntaxHighlighting', type: 'boolean', default: true, description: 'Copies with the colours.', category: 'Text Editor', scope: lang },
  { key: 'editor.accessibilitySupport', type: 'string', default: 'auto', enum: ['auto', 'on', 'off'], description: 'Whether the editor is tuned for screen readers.', category: 'Text Editor' },
  // Formatting
  { key: 'editor.defaultFormatter', type: 'string', default: '', description: 'The formatter used: its id here (prettier, ruff, black, gofmt, rustfmt, clang-format, dotnet-format, or one of config.json\'s "formatters"), or VS Code\'s extension id (esbenp.prettier-vscode). Empty: the first found for the language.', category: 'Formatting', scope: lang },
  { key: 'editor.formatOnSave', type: 'boolean', default: false, description: 'Formats a file as you save it.', category: 'Formatting', scope: lang },
  { key: 'editor.formatOnSaveMode', type: 'string', default: 'file', enum: ['file', 'modifications', 'modificationsIfAvailable'], description: 'What format on save formats. Code Arena formats the whole file in each mode.', category: 'Formatting', scope: lang },
  { key: 'editor.formatOnPaste', type: 'boolean', default: false, description: 'Formats what is pasted (with a formatter that formats a part of a file).', category: 'Formatting', scope: lang },
  { key: 'codeArena.formatOnSaveTimeout', type: 'integer', default: 1500, minimum: 100, maximum: 30000, description: 'Milliseconds format on save waits for the formatter; past it the file is saved as it is.', category: 'Formatting' },
  // Files
  { key: 'files.autoSave', type: 'string', default: 'off', enum: ['off', 'afterDelay', 'onFocusChange', 'onWindowChange'], enumDescriptions: ['Never.', 'A while after the last change ("files.autoSaveDelay").', 'When the editor loses the focus.', 'When the browser window loses the focus.'], description: 'Saves files without being asked.', category: 'Files' },
  { key: 'files.autoSaveDelay', type: 'integer', default: 1000, minimum: 0, description: 'Milliseconds after the last change "afterDelay" saves.', category: 'Files' },
  { key: 'files.trimTrailingWhitespace', type: 'boolean', default: false, description: 'Removes the spaces at the end of lines as a file is saved.', category: 'Files', scope: lang },
  { key: 'files.insertFinalNewline', type: 'boolean', default: false, description: 'Ends a file with a newline as it is saved.', category: 'Files', scope: lang },
  { key: 'files.trimFinalNewlines', type: 'boolean', default: false, description: 'Removes the newlines after the last one at the end of a file as it is saved.', category: 'Files', scope: lang },
  { key: 'files.eol', type: 'string', default: 'auto', enum: ['\n', '\r\n', 'auto'], enumDescriptions: ['LF', 'CRLF', "The system's"], description: 'The line ending of new files.', category: 'Files', scope: lang },
  { key: 'files.associations', type: 'object', default: {}, description: 'Languages for files by name or pattern, e.g. {"*.inc": "php"}.', category: 'Files' },
  // Workbench
  { key: 'workbench.colorTheme', type: 'string', default: 'Code Arena Dark', enum: themeNames, description: 'The colour theme (Ctrl+K Ctrl+T). VS Code\'s theme names read as the nearest of these.', category: 'Workbench' },
  { key: 'window.autoDetectColorScheme', type: 'boolean', default: true, description: 'Follows the system\'s light or dark: "workbench.preferredLightColorTheme" or "workbench.preferredDarkColorTheme". (VS Code: off.)', category: 'Workbench' },
  { key: 'workbench.preferredDarkColorTheme', type: 'string', default: 'Code Arena Dark', enum: themeNames, description: 'The theme when the system is dark, with "window.autoDetectColorScheme".', category: 'Workbench' },
  { key: 'workbench.preferredLightColorTheme', type: 'string', default: 'Code Arena Light', enum: themeNames, description: 'The theme when the system is light, with "window.autoDetectColorScheme".', category: 'Workbench' },
  // Diff editor
  { key: 'diffEditor.renderSideBySide', type: 'boolean', default: true, description: 'Shows a compare side by side; off: inline.', category: 'Diff Editor' },
  { key: 'diffEditor.ignoreTrimWhitespace', type: 'boolean', default: true, description: 'Ignores spaces at lines\' starts and ends in a compare.', category: 'Diff Editor' },
  { key: 'diffEditor.hideUnchangedRegions.enabled', type: 'boolean', default: false, description: 'Folds the lines a compare leaves unchanged.', category: 'Diff Editor' },
  { key: 'diffEditor.renderIndicators', type: 'boolean', default: true, description: 'Shows + and − beside changed lines.', category: 'Diff Editor' },
  { key: 'diffEditor.wordWrap', type: 'string', default: 'inherit', enum: ['off', 'on', 'inherit'], description: 'How lines wrap in a compare; inherit: as "editor.wordWrap".', category: 'Diff Editor' },
  { key: 'diffEditor.maxComputationTime', type: 'integer', default: 5000, minimum: 0, description: 'Milliseconds a compare is worked out for at most; 0: no limit.', category: 'Diff Editor' },
  // Terminal
  { key: 'terminal.integrated.fontSize', type: 'number', default: 13, minimum: 6, maximum: 100, description: 'The terminal\'s font size. (VS Code: 14.)', category: 'Terminal' },
  { key: 'terminal.integrated.fontFamily', type: 'string', default: '', description: 'The terminal\'s font family; empty: the editor\'s.', category: 'Terminal' },
  { key: 'terminal.integrated.lineHeight', type: 'number', default: 1, minimum: 1, description: 'The terminal\'s line height, a multiple of the font size.', category: 'Terminal' },
  { key: 'terminal.integrated.cursorStyle', type: 'string', default: 'block', enum: ['block', 'line', 'underline'], description: 'The terminal\'s cursor.', category: 'Terminal' },
  { key: 'terminal.integrated.cursorBlinking', type: 'boolean', default: true, description: 'Whether the terminal\'s cursor blinks. (VS Code: off.)', category: 'Terminal' },
  { key: 'terminal.integrated.scrollback', type: 'integer', default: 10000, minimum: 0, description: 'Lines the terminal keeps. (VS Code: 1000.)', category: 'Terminal' },
  { key: 'terminal.integrated.allowChords', type: 'boolean', default: true, description: 'Lets two-key shortcuts (Ctrl+K …) start in the terminal; off: Ctrl+K reaches the shell.', category: 'Terminal', scope: 'application' },
  { key: 'terminal.integrated.sendKeybindingsToShell', type: 'boolean', default: false, description: 'Sends every key to the shell, but those of "terminal.integrated.commandsToSkipShell".', category: 'Terminal', scope: 'application' },
  { key: 'terminal.integrated.commandsToSkipShell', type: 'array', items: 'string', default: [], description: 'Commands whose keys the workbench takes in the terminal, besides the default ones; "-command" gives a default one\'s key back to the shell.', category: 'Terminal', scope: 'application' },
  // Code Arena
  { key: 'codeArena.inlineCompletion.enabled', type: 'boolean', default: true, description: 'Code Arena\'s code completion as you type (the status bar\'s Complete).', category: 'Code Arena' },
]

export const settingDef = new Map(settingDefs.map((d) => [d.key, d]))

/** The JSON schema settings.json is checked with in the editor. */
export function settingsSchema(): Record<string, unknown> {
  const prop = (d: SettingDef) => ({
    type: d.type === 'integer' ? 'integer' : d.type,
    default: d.default,
    description: d.description,
    ...(d.enum ? { enum: d.enum, enumDescriptions: d.enumDescriptions } : {}),
    ...(d.minimum !== undefined ? { minimum: d.minimum } : {}),
    ...(d.maximum !== undefined ? { maximum: d.maximum } : {}),
    ...(d.items ? { items: { type: d.items } } : {}),
  })
  const properties = Object.fromEntries(settingDefs.map((d) => [d.key, prop(d)]))
  const language = Object.fromEntries(settingDefs.filter((d) => d.scope === 'language').map((d) => [d.key, prop(d)]))
  return {
    type: 'object',
    properties,
    patternProperties: { '^\\[.*\\]$': { type: 'object', description: 'Settings for these languages only.', properties: language } },
  }
}

/** A setting's value as written, checked against its definition: the value, or why it is not taken. */
export function check(d: SettingDef, v: unknown): string | null {
  const t = Array.isArray(v) ? 'array' : v === null ? 'null' : typeof v
  if (d.type === 'integer' ? !Number.isInteger(v) : d.type === 'number' ? t !== 'number' : t !== d.type) return `"${d.key}" takes ${d.type === 'array' ? 'an array' : d.type === 'object' ? 'an object' : `a ${d.type}`}.`
  if (d.enum && !d.enum.includes(v as string)) return `"${d.key}" is one of ${d.enum.map((e) => JSON.stringify(e)).join(', ')}.`
  if (typeof v === 'number' && ((d.minimum !== undefined && v < d.minimum) || (d.maximum !== undefined && v > d.maximum))) return `"${d.key}" is from ${d.minimum ?? '…'} to ${d.maximum ?? '…'}.`
  if (d.items && Array.isArray(v) && v.some((x) => typeof x !== d.items)) return `"${d.key}" is a list of ${d.items}s.`
  return null
}
