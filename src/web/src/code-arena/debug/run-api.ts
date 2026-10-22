import { api, ideSocketUrl } from '../http'
import type { TerminalInfo } from '../ide-api'

/** A run configuration: launch.json's, or one the project's files give. */
export interface RunConfig {
  name: string
  type: string
  request: string
  source: 'launch' | 'detected'
  program?: string | null
  command?: string | null
  preLaunchTask?: string | null
  /** False: it runs only with its debugger (reason says why). */
  supported: boolean
  reason?: string | null
  hidden?: boolean
  /** A detected one's configuration, as Add Configuration writes it. */
  configuration?: Record<string, unknown>
}

export interface RunConfigs {
  configurations: RunConfig[]
  compounds: { name: string; configurations: string[]; preLaunchTask?: string; stopAll?: boolean }[]
  errors: { file: string; line: number; message: string }[]
  trusted: boolean
  buildConfiguration: 'Debug' | 'Release'
}

export const runConfigsQuery = {
  queryKey: ['code', 'run', 'configs'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<RunConfigs>('/api/run/configs', { signal }),
  staleTime: 5_000,
}

/** A task: tasks.json's, or one the project's files give. */
export interface TaskInfo {
  label: string
  source: 'workspace' | 'detected'
  group: 'build' | 'test' | null
  isDefault: boolean
  command: string | null
  detail?: string | null
  dependsOn?: string[]
  background?: boolean
  terminal?: TerminalInfo | null
  running?: boolean
}

export interface Tasks {
  tasks: TaskInfo[]
  errors: { file: string; line: number; message: string }[]
  trusted: boolean
  buildConfiguration: 'Debug' | 'Release'
}

export const tasksQuery = {
  queryKey: ['code', 'tasks'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<Tasks>('/api/tasks', { signal }),
  staleTime: 5_000,
}

/** What the editor tells a start: the file, line and selection, and the settings (for ${config:…}). */
export interface RunContext {
  file?: string
  lineNumber?: number
  selectedText?: string
  settings?: Record<string, unknown>
}

export interface Started {
  terminal: TerminalInfo
  configuration: Record<string, unknown>
  command: string
}

/** A debug session started: the adapter, and the configuration in its words for launch or attach. */
export interface DebugStart {
  id: string
  name: string | null
  adapter: string
  adapterId: string
  configuration: Record<string, unknown>
  notes: string[]
  root: string
  rootReal: string
  pathStyle: 'posix' | 'win'
  /** launch.json's own word on the Debug Console (neverOpen, openOnSessionStart, openOnFirstSessionStart). */
  internalConsoleOptions?: string | null
}

export const startRun = (body: { name: string; noDebug: true; context: RunContext; inputs?: Record<string, string>; cols?: number; rows?: number }) => api<Started>('/api/run/start', { body })
export const startDebug = (body: { name: string; noDebug: false; context: RunContext; inputs?: Record<string, string>; cols?: number; rows?: number }) => api<DebugStart>('/api/run/start', { body })
export const stopRun = (terminal: string) => api<TerminalInfo>('/api/run/stop', { body: { terminal } })
export const runTask = (body: { label?: string; group?: 'build' | 'test'; wait?: boolean; context: RunContext; inputs?: Record<string, string>; cols?: number; rows?: number }) =>
  api<{ task: string; terminal: TerminalInfo | null; terminals: TerminalInfo[] }>('/api/tasks/run', { body })
export const stopTask = (terminal: string) => api<TerminalInfo>('/api/tasks/stop', { body: { terminal } })

export interface DebugAdapter {
  id: string
  name: string
  types: string[]
  found: boolean
  path?: string | null
  version?: string | null
  reason?: string | null
  install?: string | null
}

export const debugAdaptersQuery = {
  queryKey: ['code', 'debug', 'adapters'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<DebugAdapter[]>('/api/debug/adapters', { signal }),
  staleTime: 30_000,
}

export interface DebugSessionInfo {
  id: string
  name: string | null
  adapter: string
  type: string
  state: 'starting' | 'running' | 'stopped' | 'ended'
}

export const debugSessions = () => api<DebugSessionInfo[]>('/api/debug/sessions')
export const stopDebug = (id: string) => api('/api/debug/stop', { body: { id } })
export const debugSource = (id: string, path: string) => api<{ path: string; text: string | null; readOnly: boolean; version?: string }>(`/api/debug/source?id=${encodeURIComponent(id)}&path=${encodeURIComponent(path)}`)

/** A debug session's WebSocket, on this page's server (the page's own cookie lets it in, as the terminals'). */
export const debugSocketUrl = (id: string, where: Location = window.location) => ideSocketUrl(`/api/debug/socket?id=${encodeURIComponent(id)}`, where)

/** The person's own processes, for an attach configuration's ${command:pickProcess}. */
export const listProcesses = () => api<{ pid: number; name: string; command: string | null }[]>('/api/processes')
