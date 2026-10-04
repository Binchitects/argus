import { screen, within } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { admin, fakeApi, renderApp } from '@/test/utils'
import type { SettingsData } from './settings-model'

const settings: SettingsData = {
  groups: [
    {
      title: 'Chat bots',
      settings: [
        {
          key: 'Bots:Slack:SigningSecret', group: 'Chat bots', label: 'Slack: signing secret', help: 'From the Slack app.', type: 'secret', scope: 'live',
          options: null, min: null, max: null, patternHelp: null, unit: null, optional: true, impact: null, dangerous: false, default: null,
          value: null, isSet: true, source: 'saved', environmentValue: null, restartPending: false,
        },
      ],
    },
  ],
  restartNeeded: false,
}

describe('chat bots in the settings', () => {
  it('shows each platform set up or not, with the address to give it', async () => {
    fakeApi(admin, {
      'GET /api/admin/config': () => ({ json: settings }),
      'GET /api/admin/bots': () => ({
        json: {
          platforms: [
            { name: 'slack', title: 'Slack', ready: true, url: 'https://llm.test/api/bots/slack', threads: 3 },
            { name: 'teams', title: 'Microsoft Teams', ready: false, url: 'https://llm.test/api/bots/teams', threads: 0 },
            { name: 'email', title: 'Email', ready: false, url: 'https://llm.test/api/mail/inbound', threads: 0 },
          ],
        },
      }),
    })
    renderApp('/admin/settings#chat-bots')
    const section = await screen.findByRole('region', { name: 'Where the platforms reach the app' })
    const slack = within(section).getByRole('listitem', { name: 'Slack' })
    expect(slack).toHaveTextContent('Set up')
    expect(slack).toHaveTextContent('https://llm.test/api/bots/slack')
    expect(slack).toHaveTextContent('3 chats')
    expect(slack).toHaveTextContent('Request URL')
    expect(within(section).getByRole('listitem', { name: 'Microsoft Teams' })).toHaveTextContent('Not set up')
    expect(within(section).getByRole('listitem', { name: 'Email' })).toHaveTextContent('https://llm.test/api/mail/inbound')
  })
})
