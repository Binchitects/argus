import type { ComponentType } from 'react'
import type { RouteObject } from 'react-router'
import { HomePage } from '@/pages/home'
import { NotFoundPage } from '@/pages/not-found'
import { RouteErrorPage } from '@/pages/route-error'
import { RequireAdmin } from './require-admin'
import { Shell } from './shell'

/**
 * A page's code, fetched when it is first opened. One retry, half a second
 * later, rides out a dropped connection (or a browser reloading its
 * certificates) before the page says it did not load.
 */
const lazy = (load: () => Promise<{ Component: ComponentType }>) => ({
  lazy: () => load().catch(() => new Promise<void>((r) => setTimeout(r, 500)).then(load)),
})

export const routes: RouteObject[] = [
  { path: '/login', ...lazy(() => import('@/pages/login').then((m) => ({ Component: m.LoginPage }))), errorElement: <RouteErrorPage /> },
  // Code Studio's IDE: the whole window, under its own bar (not the app's).
  { path: '/code/w/:id', ...lazy(() => import('@/pages/code/studio').then((m) => ({ Component: m.WorkspacePage }))), errorElement: <RouteErrorPage /> },
  {
    path: '/',
    element: <Shell />,
    errorElement: <RouteErrorPage />,
    children: [
      {
        errorElement: <RouteErrorPage />,
        children: [
          { index: true, element: <HomePage /> },
          { path: 'account', ...lazy(() => import('@/pages/account').then((m) => ({ Component: m.AccountPage }))) },
          // The browser extension hands a page or a selection over here (lib/handoff).
          { path: 'ask', ...lazy(() => import('@/pages/ask').then((m) => ({ Component: m.AskPage }))) },
          { path: 'design', ...lazy(() => import('@/pages/design').then((m) => ({ Component: m.DesignPage }))) },
          // The chat uses the whole window below the top bar.
          { path: 'chat', handle: { fullBleed: true }, ...lazy(() => import('@/pages/chat/chat-page').then((m) => ({ Component: m.ChatPage }))) },
          { path: 'chat/:id', handle: { fullBleed: true }, ...lazy(() => import('@/pages/chat/chat-page').then((m) => ({ Component: m.ChatPage }))) },
          { path: 'chat/assistants/:assistantId', handle: { fullBleed: true }, ...lazy(() => import('@/pages/chat/chat-page').then((m) => ({ Component: m.ChatPage }))) },
          { path: 'shared/:shareId', handle: { fullBleed: true }, ...lazy(() => import('@/pages/chat/shared-view').then((m) => ({ Component: m.SharedChatPage }))) },
          { path: 'assistants', ...lazy(() => import('@/pages/assistants').then((m) => ({ Component: m.AssistantsPage }))) },
          { path: 'skills', ...lazy(() => import('@/pages/skills').then((m) => ({ Component: m.SkillsPage }))) },
          { path: 'checklists', ...lazy(() => import('@/pages/checklists').then((m) => ({ Component: m.ChecklistsPage }))) },
          { path: 'memory', ...lazy(() => import('@/pages/memory').then((m) => ({ Component: m.MemoryPage }))) },
          { path: 'meetings', ...lazy(() => import('@/pages/meetings/meetings').then((m) => ({ Component: m.MeetingsPage }))) },
          { path: 'meetings/:id', ...lazy(() => import('@/pages/meetings/meeting').then((m) => ({ Component: m.MeetingPage }))) },
          { path: 'plans', ...lazy(() => import('@/pages/plans').then((m) => ({ Component: m.PlansPage }))) },
          { path: 'lab', ...lazy(() => import('@/pages/lab').then((m) => ({ Component: m.LabPage }))) },
          { path: 'lab/:id', ...lazy(() => import('@/pages/lab').then((m) => ({ Component: m.LabNotebookPage }))) },
          { path: 'apps', ...lazy(() => import('@/pages/apps/apps').then((m) => ({ Component: m.AppsPage }))) },
          { path: 'apps/:id', ...lazy(() => import('@/pages/apps/apps').then((m) => ({ Component: m.AppPage }))) },
          { path: 'data', ...lazy(() => import('@/pages/data/data').then((m) => ({ Component: m.DataPage }))) },
          { path: 'data/boards', ...lazy(() => import('@/pages/data/boards').then((m) => ({ Component: m.BoardsPage }))) },
          { path: 'data/boards/:id', ...lazy(() => import('@/pages/data/boards').then((m) => ({ Component: m.BoardPage }))) },
          { path: 'decks', ...lazy(() => import('@/pages/decks/decks').then((m) => ({ Component: m.DecksPage }))) },
          { path: 'decks/:id', ...lazy(() => import('@/pages/decks/decks').then((m) => ({ Component: m.DeckPage }))) },
          { path: 'media', ...lazy(() => import('@/pages/media/media').then((m) => ({ Component: m.MediaPage }))) },
          { path: 'studio', ...lazy(() => import('@/pages/studio/studio').then((m) => ({ Component: m.StudioPage }))) },
          { path: 'studio/:id', ...lazy(() => import('@/pages/studio/workflow').then((m) => ({ Component: m.WorkflowPage }))) },
          { path: 'rooms/join/:token', ...lazy(() => import('@/pages/room-join').then((m) => ({ Component: m.RoomJoinPage }))) },
          { path: 'plans/:id', ...lazy(() => import('@/pages/plans').then((m) => ({ Component: m.PlanPage }))) },
          { path: 'code', ...lazy(() => import('@/pages/code/code').then((m) => ({ Component: m.CodePage }))) },
          { path: 'code/reviews/:id', ...lazy(() => import('@/pages/code/review').then((m) => ({ Component: m.ReviewPage }))) },
          { path: 'code/security/:id', ...lazy(() => import('@/pages/code/security').then((m) => ({ Component: m.SecurityScanPage }))) },
          { path: 'code/business/:id', ...lazy(() => import('@/pages/code/business').then((m) => ({ Component: m.BusinessPage }))) },
          { path: 'code/fixes', ...lazy(() => import('@/pages/code/fixes').then((m) => ({ Component: m.FixesPage }))) },
          { path: 'code/fixes/:id', ...lazy(() => import('@/pages/code/fixes').then((m) => ({ Component: m.FixPage }))) },
          { path: 'tasks', ...lazy(() => import('@/pages/tasks').then((m) => ({ Component: m.TasksPage }))) },
          { path: 'usage', ...lazy(() => import('@/pages/usage').then((m) => ({ Component: m.UsagePage }))) },
          { path: 'leaderboard', ...lazy(() => import('@/pages/leaderboard').then((m) => ({ Component: m.LeaderboardPage }))) },
          { path: 'setup', ...lazy(() => import('@/pages/setup').then((m) => ({ Component: m.ConnectPage }))) },
          { path: 'help/*', ...lazy(() => import('@/pages/help').then((m) => ({ Component: m.ManualPage }))) },
          {
            path: 'admin',
            element: <RequireAdmin />,
            children: [
              { index: true, ...lazy(() => import('@/pages/admin/overview').then((m) => ({ Component: m.OverviewPage }))) },
              { path: 'people', ...lazy(() => import('@/pages/admin/people').then((m) => ({ Component: m.PeoplePage }))) },
              { path: 'people/:id', ...lazy(() => import('@/pages/admin/person').then((m) => ({ Component: m.PersonPage }))) },
              { path: 'access', ...lazy(() => import('@/pages/admin/access').then((m) => ({ Component: m.AccessPage }))) },
              { path: 'safeguards', ...lazy(() => import('@/pages/admin/safeguards').then((m) => ({ Component: m.SafeguardsPage }))) },
              { path: 'groups', ...lazy(() => import('@/pages/admin/groups').then((m) => ({ Component: m.GroupsPage }))) },
              { path: 'groups/:id', ...lazy(() => import('@/pages/admin/group').then((m) => ({ Component: m.GroupPage }))) },
              { path: 'tools', ...lazy(() => import('@/pages/admin/tools').then((m) => ({ Component: m.ToolsPage }))) },
              { path: 'plugins', ...lazy(() => import('@/pages/admin/plugins').then((m) => ({ Component: m.PluginsPage }))) },
              { path: 'knowledge', ...lazy(() => import('@/pages/admin/knowledge').then((m) => ({ Component: m.KnowledgePage }))) },
              { path: 'data-sources', ...lazy(() => import('@/pages/admin/data-sources').then((m) => ({ Component: m.DataSourcesPage }))) },
              { path: 'storage', ...lazy(() => import('@/pages/admin/storage').then((m) => ({ Component: m.StoragePage }))) },
              { path: 'sign-in', ...lazy(() => import('@/pages/admin/sign-in').then((m) => ({ Component: m.SignInPage }))) },
              { path: 'models', ...lazy(() => import('@/pages/admin/models').then((m) => ({ Component: m.ModelsPage }))) },
              { path: 'settings', ...lazy(() => import('@/pages/admin/settings').then((m) => ({ Component: m.SettingsPage }))) },
              { path: 'audit', ...lazy(() => import('@/pages/admin/audit').then((m) => ({ Component: m.AuditPage }))) },
              { path: 'compliance', ...lazy(() => import('@/pages/admin/compliance').then((m) => ({ Component: m.CompliancePage }))) },
              { path: 'compliance/incidents/:id', ...lazy(() => import('@/pages/admin/incidents').then((m) => ({ Component: m.IncidentPage }))) },
              { path: 'compliance/reviews/:id', ...lazy(() => import('@/pages/admin/access-review').then((m) => ({ Component: m.AccessReviewPage }))) },
              { path: 'security', ...lazy(() => import('@/pages/admin/security').then((m) => ({ Component: m.SecurityPage }))) },
              { path: 'quality', ...lazy(() => import('@/pages/admin/quality').then((m) => ({ Component: m.QualityPage }))) },
              { path: 'indexing', ...lazy(() => import('@/pages/admin/argus').then((m) => ({ Component: m.IndexingPage }))) },
              { path: 'packs', ...lazy(() => import('@/pages/admin/argus-packs').then((m) => ({ Component: m.PacksPage }))) },
              { path: 'explore', ...lazy(() => import('@/pages/admin/argus-explore').then((m) => ({ Component: m.ExplorePage }))) },
              { path: 'monitoring', ...lazy(() => import('@/pages/admin/monitoring').then((m) => ({ Component: m.MonitoringPage }))) },
              { path: 'line', ...lazy(() => import('@/pages/admin/line').then((m) => ({ Component: m.LinePage }))) },
              { path: 'feedback', ...lazy(() => import('@/pages/admin/feedback').then((m) => ({ Component: m.FeedbackPage }))) },
              { path: 'prompts', ...lazy(() => import('@/pages/admin/prompts').then((m) => ({ Component: m.PromptLogPage }))) },
              { path: 'dashboards', ...lazy(() => import('@/pages/admin/dashboards').then((m) => ({ Component: m.DashboardsPage }))) },
              { path: 'dashboards/:uid', ...lazy(() => import('@/pages/admin/dashboards').then((m) => ({ Component: m.DashboardPage }))) },
              { path: 'logs', ...lazy(() => import('@/pages/admin/logs').then((m) => ({ Component: m.LogsPage }))) },
              { path: 'alerts', ...lazy(() => import('@/pages/admin/alerts').then((m) => ({ Component: m.AlertsPage }))) },
              { path: 'traces', ...lazy(() => import('@/pages/admin/traces').then((m) => ({ Component: m.TracesPage }))) },
              { path: '*', element: <NotFoundPage /> },
            ],
          },
          { path: '*', element: <NotFoundPage /> },
        ],
      },
    ],
  },
]
