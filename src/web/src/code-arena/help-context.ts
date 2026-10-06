import { createContext, useContext } from 'react'
import type { Part } from './help-text'

/** Opens Code Arena's help, at a part of the workbench or at the whole (help.tsx, HelpProvider). */
export const OpenHelp = createContext<((part?: Part) => void) | null>(null)

/** What opens the help; null outside the workbench (a panel drawn on its own). */
export const useOpenHelp = () => useContext(OpenHelp)
